using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

sealed partial class StandaloneSystemAlertsReader
{
    private const string ApplicationServerSource = "Application Server";
    private const string DataServerSource = "Data Server";
    private const string HealthyStatus = "Healthy";
    private const string WarningStatus = "Warning";
    private const string CriticalStatus = "Critical";
    private const string HighSeverity = "High";
    private const string MediumSeverity = "Medium";
    private const string NotReported = "Not reported";

    private readonly SystemHealthOptions _options;

    public StandaloneSystemAlertsReader(SystemHealthOptions options)
    {
        _options = options;
    }

    public SystemAlertsSnapshot Get()
    {
        var generatedAtUtc = DateTime.UtcNow;
        var options = Normalize(_options.SystemAlerts);
        var alerts = new List<SystemAlert>();
        var checks = new List<SystemHealthCheck>();
        var dataServerChecks = new List<SystemHealthCheck>();

        var drives = LoadDriveMetrics(options, ApplicationServerSource, alerts, generatedAtUtc);
        var dataServerDrives = BuildMissingDataServerDrives(options, dataServerChecks, alerts, generatedAtUtc);
        var processCpu = GetCurrentProcessCpuPercent();
        var memoryUsage = GetMemoryUsagePercent();

        AddThresholdCheck(
            checks,
            alerts,
            generatedAtUtc,
            "SystemHealth process CPU",
            ApplicationServerSource,
            processCpu,
            options.ProcessCpuWarningPercent,
            options.ProcessCpuCriticalPercent,
            "%",
            "SystemHealth API process CPU usage.");

        AddThresholdCheck(
            checks,
            alerts,
            generatedAtUtc,
            "Application server memory",
            ApplicationServerSource,
            memoryUsage,
            options.MemoryWarningPercent,
            options.MemoryCriticalPercent,
            "%",
            memoryUsage < 0
                ? "Memory usage is unavailable on this operating system."
                : "Windows physical memory usage.");

        AddFolderCheck(checks, alerts, generatedAtUtc, "Deployment root", "Deployment", options.DeploymentRootPath, mustExist: true);
        checks.Add(Check("MSSQL dependency", "Runtime", HealthyStatus, "No MSSQL connection is required for the standalone SystemHealth app."));
        checks.Add(Check("Access gate dependency", "Runtime", HealthyStatus, "No interactive access gate or database-backed user state is required."));

        var critical = alerts.Count(alert => string.Equals(alert.Severity, HighSeverity, StringComparison.OrdinalIgnoreCase));
        var warnings = alerts.Count(alert => string.Equals(alert.Severity, MediumSeverity, StringComparison.OrdinalIgnoreCase));
        var failures = checks.Count(check => string.Equals(check.Status, WarningStatus, StringComparison.OrdinalIgnoreCase));
        var status = ResolveSnapshotStatus(critical, alerts.Count);

        return new SystemAlertsSnapshot
        {
            GeneratedAtUtc = generatedAtUtc,
            Status = status,
            StatusDetail = status switch
            {
                HealthyStatus => "Application server drives, runtime checks, deployment root, and standalone dependencies are currently healthy.",
                CriticalStatus => "One or more critical System Alert checks need immediate attention.",
                _ => "One or more System Alert checks need attention."
            },
            Summary = new SystemAlertsSummary
            {
                Critical = critical,
                Warnings = warnings,
                Failures = failures,
                ProcessCpuPercent = processCpu,
                MemoryUsagePercent = Math.Max(0, memoryUsage),
                DataServerCpuPercent = 0,
                DataServerMemoryUsagePercent = 0
            },
            ApplicationDrives = drives,
            DataServerDrives = dataServerDrives,
            Checks = checks,
            DataServerChecks = dataServerChecks,
            Alerts = alerts.OrderByDescending(alert => alert.TimestampUtc).ThenBy(alert => alert.Source, StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static SystemDriveMetric[] LoadDriveMetrics(
        SystemAlertsOptions options,
        string source,
        List<SystemAlert> alerts,
        DateTime timestampUtc)
    {
        var reportedDrives = DriveInfo.GetDrives()
            .ToDictionary(drive => NormalizeDriveName(drive.Name), StringComparer.OrdinalIgnoreCase);

        var driveNames = options.ApplicationServerDriveLetters
            .Select(NormalizeDriveName)
            .Concat(reportedDrives.Keys)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var drives = driveNames
            .Select(name => CreateApplicationDriveMetric(name, options, reportedDrives))
            .ToArray();

        AddDriveAlerts(drives, source, options, alerts, timestampUtc);
        return drives;
    }

    private static SystemDriveMetric CreateApplicationDriveMetric(
        string name,
        SystemAlertsOptions options,
        IReadOnlyDictionary<string, DriveInfo> reportedDrives)
    {
        if (TryCreateReportedDriveMetric(name, reportedDrives, options, out var metric)
            || TryCreateDriveMetric(name, options, out metric))
        {
            return metric;
        }

        return MissingDriveMetric(name);
    }

    private static bool TryCreateReportedDriveMetric(
        string name,
        IReadOnlyDictionary<string, DriveInfo> reportedDrives,
        SystemAlertsOptions options,
        out SystemDriveMetric metric)
    {
        metric = new SystemDriveMetric();
        if (!reportedDrives.TryGetValue(name, out var drive))
        {
            return false;
        }

        try
        {
            return TryCreateDriveMetric(drive, options, out metric);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryCreateDriveMetric(string driveName, SystemAlertsOptions options, out SystemDriveMetric metric)
    {
        metric = new SystemDriveMetric();
        try
        {
            return TryCreateDriveMetric(new DriveInfo(driveName), options, out metric);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryCreateDriveMetric(DriveInfo drive, SystemAlertsOptions options, out SystemDriveMetric metric)
    {
        metric = new SystemDriveMetric();
        if (!drive.IsReady)
        {
            return false;
        }

        metric = CreateDriveMetric(drive.Name, drive.TotalSize, drive.TotalFreeSpace, options);
        return true;
    }

    private static SystemDriveMetric CreateDriveMetric(string name, long totalBytes, long freeBytes, SystemAlertsOptions options)
    {
        var usedBytes = Math.Max(0, totalBytes - freeBytes);
        var usage = totalBytes <= 0
            ? 0
            : (int)Math.Round(100 - ((double)freeBytes / totalBytes * 100));
        var status = usage >= options.DiskCriticalPercent || usage >= options.DiskWarningPercent
            ? WarningStatus
            : HealthyStatus;

        return new SystemDriveMetric
        {
            Name = NormalizeDriveName(name),
            Total = FormatBytes(totalBytes),
            Used = FormatBytes(usedBytes),
            Free = FormatBytes(freeBytes),
            UsagePercent = usage,
            Status = status
        };
    }

    private static SystemDriveMetric MissingDriveMetric(string name)
    {
        return new SystemDriveMetric
        {
            Name = name,
            Total = NotReported,
            Used = NotReported,
            Free = NotReported,
            UsagePercent = 0,
            Status = WarningStatus
        };
    }

    private static SystemDriveMetric[] BuildMissingDataServerDrives(
        SystemAlertsOptions options,
        List<SystemHealthCheck> checks,
        List<SystemAlert> alerts,
        DateTime timestampUtc)
    {
        checks.Add(Check("Data Server metrics endpoint", DataServerSource, WarningStatus, "Data Server metrics endpoint is not configured."));

        var drives = options.DataServerDriveLetters
            .Select(NormalizeDriveName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(MissingDriveMetric)
            .ToArray();

        AddDriveAlerts(drives, DataServerSource, options, alerts, timestampUtc);
        return drives;
    }

    private static void AddDriveAlerts(
        IEnumerable<SystemDriveMetric> drives,
        string source,
        SystemAlertsOptions options,
        List<SystemAlert> alerts,
        DateTime timestampUtc)
    {
        foreach (var drive in drives.Where(drive => string.Equals(drive.Status, WarningStatus, StringComparison.OrdinalIgnoreCase)))
        {
            var severity = drive.UsagePercent >= options.DiskCriticalPercent ? HighSeverity : MediumSeverity;
            var message = drive.Total == NotReported
                ? $"Drive {drive.Name} ({source}) is not reporting storage metrics."
                : $"Drive {drive.Name} ({source}) is {drive.UsagePercent}% full.";
            alerts.Add(Alert($"drive-{ToAlertId(source)}-{ToAlertId(drive.Name)}", source, severity, message, timestampUtc));
        }
    }

    private static void AddThresholdCheck(
        List<SystemHealthCheck> checks,
        List<SystemAlert> alerts,
        DateTime timestampUtc,
        string name,
        string category,
        int value,
        int warningThreshold,
        int criticalThreshold,
        string suffix,
        string healthyDetail)
    {
        if (value < 0)
        {
            AddCheck(checks, alerts, timestampUtc, name, category, WarningStatus, healthyDetail, MediumSeverity);
            return;
        }

        if (value >= criticalThreshold)
        {
            AddCheck(checks, alerts, timestampUtc, name, category, WarningStatus, $"{name} is {value}{suffix}.", HighSeverity);
            return;
        }

        if (value >= warningThreshold)
        {
            AddCheck(checks, alerts, timestampUtc, name, category, WarningStatus, $"{name} is {value}{suffix}.", MediumSeverity);
            return;
        }

        checks.Add(Check(name, category, HealthyStatus, $"{healthyDetail} Current value: {value}{suffix}."));
    }

    private static void AddFolderCheck(
        List<SystemHealthCheck> checks,
        List<SystemAlert> alerts,
        DateTime timestampUtc,
        string name,
        string category,
        string path,
        bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            AddCheck(checks, alerts, timestampUtc, name, category, WarningStatus, $"{name} path is not configured.", HighSeverity);
            return;
        }

        var exists = Directory.Exists(path);
        if (exists == mustExist)
        {
            checks.Add(Check(name, category, HealthyStatus, $"{path} is present."));
            return;
        }

        AddCheck(checks, alerts, timestampUtc, name, category, WarningStatus, $"{path} is missing.", HighSeverity);
    }

    private static void AddCheck(
        List<SystemHealthCheck> checks,
        List<SystemAlert> alerts,
        DateTime timestampUtc,
        string name,
        string category,
        string status,
        string detail,
        string severity)
    {
        checks.Add(Check(name, category, status, detail));
        alerts.Add(Alert(ToAlertId(name), category, severity, detail, timestampUtc));
    }

    private static SystemHealthCheck Check(string name, string category, string status, string detail)
    {
        return new SystemHealthCheck
        {
            Name = name,
            Category = category,
            Status = status,
            Detail = detail
        };
    }

    private static SystemAlert Alert(string id, string source, string severity, string message, DateTime timestampUtc)
    {
        return new SystemAlert
        {
            Id = id,
            Source = source,
            Severity = severity,
            Message = message,
            TimestampUtc = timestampUtc,
            Acknowledged = false
        };
    }

    private static int GetCurrentProcessCpuPercent()
    {
        using var process = Process.GetCurrentProcess();
        return (int)Math.Clamp(Math.Round(process.TotalProcessorTime.TotalMilliseconds / Math.Max(1, Environment.TickCount64) * 100 / Environment.ProcessorCount), 0, 100);
    }

    private static int GetMemoryUsagePercent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return -1;
        }

        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
        {
            return -1;
        }

        return (int)Math.Clamp(Math.Round(100 - ((double)status.AvailPhys / status.TotalPhys * 100)), 0, 100);
    }

    private static string ResolveSnapshotStatus(int criticalAlerts, int totalAlerts)
    {
        if (criticalAlerts > 0)
        {
            return CriticalStatus;
        }

        return totalAlerts > 0 ? WarningStatus : HealthyStatus;
    }

    private static SystemAlertsOptions Normalize(SystemAlertsOptions options)
    {
        return new SystemAlertsOptions
        {
            DiskWarningPercent = Math.Clamp(options.DiskWarningPercent, 1, 100),
            DiskCriticalPercent = Math.Clamp(options.DiskCriticalPercent, 1, 100),
            MemoryWarningPercent = Math.Clamp(options.MemoryWarningPercent, 1, 100),
            MemoryCriticalPercent = Math.Clamp(options.MemoryCriticalPercent, 1, 100),
            ProcessCpuWarningPercent = Math.Clamp(options.ProcessCpuWarningPercent, 1, 100),
            ProcessCpuCriticalPercent = Math.Clamp(options.ProcessCpuCriticalPercent, 1, 100),
            DeploymentRootPath = string.IsNullOrWhiteSpace(options.DeploymentRootPath)
                ? AppContext.BaseDirectory
                : options.DeploymentRootPath,
            ApplicationServerDriveLetters = NormalizeDriveLetters(options.ApplicationServerDriveLetters),
            DataServerDriveLetters = NormalizeDriveLetters(options.DataServerDriveLetters)
        };
    }

    private static string[] NormalizeDriveLetters(string[]? driveLetters)
    {
        return (driveLetters ?? [])
            .Select(NormalizeDriveName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeDriveName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var name = value.Trim().Replace('/', '\\');
        if (name.Length == 1 && char.IsLetter(name[0]))
        {
            return $"{char.ToUpperInvariant(name[0])}:\\";
        }

        if (name.Length == 2 && char.IsLetter(name[0]) && name[1] == ':')
        {
            return $"{char.ToUpperInvariant(name[0])}:\\";
        }

        if (name.Length >= 3 && char.IsLetter(name[0]) && name[1] == ':' && name[2] == '\\')
        {
            return $"{char.ToUpperInvariant(name[0])}:\\";
        }

        return name;
    }

    private static string FormatBytes(long bytes)
    {
        const double gb = 1024d * 1024d * 1024d;
        return $"{Math.Round(bytes / gb, 2)} GB";
    }

    private static string ToAlertId(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '-');
        }

        return builder.ToString().Trim('-');
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}

sealed class SystemAlertsSnapshot
{
    public DateTime GeneratedAtUtc { get; set; }
    public string Status { get; set; } = HealthyStatus;
    public string StatusDetail { get; set; } = string.Empty;
    public SystemAlertsSummary Summary { get; set; } = new();
    public IReadOnlyList<SystemDriveMetric> ApplicationDrives { get; set; } = [];
    public IReadOnlyList<SystemDriveMetric> DataServerDrives { get; set; } = [];
    public IReadOnlyList<SystemHealthCheck> Checks { get; set; } = [];
    public IReadOnlyList<SystemHealthCheck> DataServerChecks { get; set; } = [];
    public IReadOnlyList<SystemAlert> Alerts { get; set; } = [];

    private const string HealthyStatus = "Healthy";
}

sealed class SystemAlertsSummary
{
    public int Critical { get; set; }
    public int Warnings { get; set; }
    public int Failures { get; set; }
    public int ProcessCpuPercent { get; set; }
    public int MemoryUsagePercent { get; set; }
    public int DataServerCpuPercent { get; set; }
    public int DataServerMemoryUsagePercent { get; set; }
}

sealed class SystemDriveMetric
{
    public string Name { get; set; } = string.Empty;
    public string Total { get; set; } = string.Empty;
    public string Used { get; set; } = string.Empty;
    public string Free { get; set; } = string.Empty;
    public int UsagePercent { get; set; }
    public string Status { get; set; } = "Healthy";
}

sealed class SystemHealthCheck
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Status { get; set; } = "Healthy";
    public string Detail { get; set; } = string.Empty;
}

sealed class SystemAlert
{
    public string Id { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public bool Acknowledged { get; set; }
}
