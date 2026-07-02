using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SystemHealth.Api;

internal interface IAdminEnvironmentUptimeProvider
{
    string GetUptime(AdminEnvironmentTargetOptions target, DateTime checkedAtUtc);
}

internal sealed partial class IisAdminEnvironmentUptimeProvider : IAdminEnvironmentUptimeProvider
{
    private const string NotConfigured = "Not configured";
    private const string Unavailable = "Unavailable";
    private readonly Func<bool> _isWindows;
    private readonly Func<IReadOnlyCollection<IisWorkerProcessInfo>> _workerProcessesProvider;
    private readonly Func<int, DateTime> _processStartTimeUtcProvider;

    public IisAdminEnvironmentUptimeProvider()
        : this(
            IsWindows,
            GetWorkerProcesses,
            GetProcessStartTimeUtc)
    {
    }

    internal IisAdminEnvironmentUptimeProvider(
        Func<bool> isWindows,
        Func<IReadOnlyCollection<IisWorkerProcessInfo>> workerProcessesProvider,
        Func<int, DateTime> processStartTimeUtcProvider)
    {
        _isWindows = isWindows;
        _workerProcessesProvider = workerProcessesProvider;
        _processStartTimeUtcProvider = processStartTimeUtcProvider;
    }

    public string GetUptime(AdminEnvironmentTargetOptions target, DateTime checkedAtUtc)
    {
        var appPoolName = target.UptimeAppPoolName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(appPoolName))
        {
            return NotConfigured;
        }

        if (!IsSafeAppPoolName(appPoolName))
        {
            return Unavailable;
        }

        if (!_isWindows())
        {
            return Unavailable;
        }

        try
        {
            var workerProcess = FindWorkerProcess(_workerProcessesProvider(), appPoolName);
            if (workerProcess is null)
            {
                return "App pool idle";
            }

            var startedAtUtc = _processStartTimeUtcProvider(workerProcess.ProcessId);
            var uptime = checkedAtUtc - startedAtUtc;
            return FormatUptime(uptime);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or IOException or ArgumentException or ManagementException or COMException)
        {
            return BuildUnavailable(ex.Message);
        }
    }

    private static IReadOnlyCollection<IisWorkerProcessInfo> GetWorkerProcesses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<IisWorkerProcessInfo>();
        }

        using var searcher = new ManagementObjectSearcher(
            @"root\WebAdministration",
            "SELECT AppPoolName, ProcessId FROM WorkerProcess");
        using var results = searcher.Get();
        var workerProcesses = new List<IisWorkerProcessInfo>();
        foreach (ManagementObject workerProcess in results)
        {
            using (workerProcess)
            {
                var appPoolName = Convert.ToString(workerProcess["AppPoolName"], CultureInfo.InvariantCulture) ?? string.Empty;
                var processId = Convert.ToInt32(workerProcess["ProcessId"], CultureInfo.InvariantCulture);
                workerProcesses.Add(new IisWorkerProcessInfo(appPoolName, processId));
            }
        }

        return workerProcesses;
    }

    private static IisWorkerProcessInfo? FindWorkerProcess(IEnumerable<IisWorkerProcessInfo> workerProcesses, string appPoolName)
    {
        foreach (var workerProcess in workerProcesses)
        {
            if (string.Equals(workerProcess.AppPoolName.Trim(), appPoolName, StringComparison.OrdinalIgnoreCase))
            {
                return workerProcess;
            }
        }

        return null;
    }

    internal static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    internal static DateTime GetProcessStartTimeUtc(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return process.StartTime.ToUniversalTime();
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime <= TimeSpan.Zero)
        {
            return "Just started";
        }

        if (uptime.TotalDays >= 1)
        {
            var days = (int)uptime.TotalDays;
            var hours = uptime.Hours;
            return hours == 0
                ? $"{days} day{Plural(days)}"
                : $"{days} day{Plural(days)} {hours} hr";
        }

        if (uptime.TotalHours >= 1)
        {
            var hours = (int)uptime.TotalHours;
            var minutes = uptime.Minutes;
            return minutes == 0
                ? $"{hours} hr"
                : $"{hours} hr {minutes} min";
        }

        var totalMinutes = Math.Max(1, (int)uptime.TotalMinutes);
        return $"{totalMinutes} min";
    }

    private static string Plural(int value) => value == 1 ? string.Empty : "s";

    private static string BuildUnavailable(string detail)
    {
        var normalized = NormalizeDiagnostic(detail);
        return string.IsNullOrWhiteSpace(normalized)
            ? Unavailable
            : $"{Unavailable}: {normalized}";
    }

    private static bool IsSafeAppPoolName(string appPoolName)
    {
        return !string.IsNullOrWhiteSpace(appPoolName)
            && appPoolName.Length <= 128
            && AppPoolNameRegex().IsMatch(appPoolName);
    }

    private static string NormalizeDiagnostic(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return string.Empty;
        }

        return DiagnosticWhitespaceRegex().Replace(detail.Trim(), " ");
    }

    [GeneratedRegex("^[A-Za-z0-9._:()\\-]+$", RegexOptions.None, 1000)]
    private static partial Regex AppPoolNameRegex();

    [GeneratedRegex("\\s+", RegexOptions.None, 1000)]
    private static partial Regex DiagnosticWhitespaceRegex();
}

internal sealed record IisWorkerProcessInfo(string AppPoolName, int ProcessId);
