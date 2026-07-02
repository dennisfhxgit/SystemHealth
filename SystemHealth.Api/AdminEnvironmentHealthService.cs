using System.Diagnostics;

namespace SystemHealth.Api;

internal sealed class AdminEnvironmentHealthService
{
    private const string ErrorStatus = "Error";
    private const string SlowStatus = "Slow";
    private const string CriticalStatus = "Critical";
    private const string WarningStatus = "Warning";
    private const string HealthyStatus = "Healthy";
    private const string LiveMode = "Live";
    private const string OfflineMode = "Offline";

    private readonly HttpClient _httpClient;
    private readonly global::SystemHealthOptions _options;
    private readonly IAdminEnvironmentUptimeProvider _uptimeProvider;

    public AdminEnvironmentHealthService(
        HttpClient httpClient,
        global::SystemHealthOptions options,
        IAdminEnvironmentUptimeProvider? uptimeProvider = null)
    {
        _httpClient = httpClient;
        _options = options;
        _uptimeProvider = uptimeProvider ?? new IisAdminEnvironmentUptimeProvider();
    }

    public async Task<AdminEnvironmentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var options = Normalize(_options.AdminEnvironment);
        var generatedAtUtc = DateTime.UtcNow;

        if (options.Targets.Length == 0)
        {
            return new AdminEnvironmentSnapshotDto
            {
                GeneratedAtUtc = generatedAtUtc,
                Status = WarningStatus,
                StatusDetail = "No Admin & Environment targets are configured."
            };
        }

        var checks = new List<AdminEnvironmentStatusDto>();
        foreach (var target in options.Targets)
        {
            checks.Add(await CheckTargetAsync(target, options, cancellationToken));
        }

        var hasErrors = checks.Any(check => string.Equals(check.Status, ErrorStatus, StringComparison.OrdinalIgnoreCase));
        var hasSlow = checks.Any(check => string.Equals(check.Status, SlowStatus, StringComparison.OrdinalIgnoreCase));

        return new AdminEnvironmentSnapshotDto
        {
            GeneratedAtUtc = generatedAtUtc,
            Status = BuildSnapshotStatus(hasErrors, hasSlow),
            StatusDetail = BuildSnapshotStatusDetail(hasErrors, hasSlow),
            Environments = checks
        };
    }

    private async Task<AdminEnvironmentStatusDto> CheckTargetAsync(
        AdminEnvironmentTargetOptions target,
        AdminEnvironmentOptions options,
        CancellationToken cancellationToken)
    {
        var lastCheckedUtc = DateTime.UtcNow;
        if (!Uri.TryCreate(target.Url, UriKind.Absolute, out var uri))
        {
            var uptime = _uptimeProvider.GetUptime(target, lastCheckedUtc);
            return BuildResult(new AdminEnvironmentResultRequest
            {
                Target = target,
                Status = ErrorStatus,
                Latency = "Invalid URL",
                Mode = OfflineMode,
                Uptime = uptime,
                LastCheckedUtc = lastCheckedUtc,
                StatusCode = 0,
                Detail = "Configured URL is invalid."
            });
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.TimeoutMilliseconds);

        try
        {
            var sw = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            sw.Stop();

            var latency = Math.Max(1, sw.ElapsedMilliseconds);
            var statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var uptime = _uptimeProvider.GetUptime(target, lastCheckedUtc);
                return BuildResult(new AdminEnvironmentResultRequest
                {
                    Target = target,
                    Status = ErrorStatus,
                    Latency = $"{latency} ms",
                    Mode = LiveMode,
                    Uptime = uptime,
                    LastCheckedUtc = lastCheckedUtc,
                    StatusCode = statusCode,
                    Detail = $"HTTP {statusCode} returned."
                });
            }

            if (latency >= options.SlowThresholdMilliseconds)
            {
                var uptime = _uptimeProvider.GetUptime(target, lastCheckedUtc);
                return BuildResult(new AdminEnvironmentResultRequest
                {
                    Target = target,
                    Status = SlowStatus,
                    Latency = $"{latency} ms",
                    Mode = LiveMode,
                    Uptime = uptime,
                    LastCheckedUtc = lastCheckedUtc,
                    StatusCode = statusCode,
                    Detail = $"Response exceeded {options.SlowThresholdMilliseconds} ms."
                });
            }

            var successUptime = _uptimeProvider.GetUptime(target, lastCheckedUtc);
            return BuildResult(new AdminEnvironmentResultRequest
            {
                Target = target,
                Status = "OK",
                Latency = $"{latency} ms",
                Mode = LiveMode,
                Uptime = successUptime,
                LastCheckedUtc = lastCheckedUtc,
                StatusCode = statusCode,
                Detail = "Health check returned success."
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            var uptime = _uptimeProvider.GetUptime(target, lastCheckedUtc);
            return BuildResult(new AdminEnvironmentResultRequest
            {
                Target = target,
                Status = ErrorStatus,
                Latency = "Timeout",
                Mode = OfflineMode,
                Uptime = uptime,
                LastCheckedUtc = lastCheckedUtc,
                StatusCode = 0,
                Detail = "Environment could not be reached before timeout."
            });
        }
    }

    private static string BuildSnapshotStatusDetail(bool hasErrors, bool hasSlow)
    {
        if (hasErrors)
        {
            return "One or more configured environments failed health checks.";
        }

        if (hasSlow)
        {
            return "One or more configured environments responded slowly.";
        }

        return "Configured environment checks are healthy.";
    }

    private static string BuildSnapshotStatus(bool hasErrors, bool hasSlow)
    {
        if (hasErrors)
        {
            return CriticalStatus;
        }

        return hasSlow ? WarningStatus : HealthyStatus;
    }

    private static AdminEnvironmentStatusDto BuildResult(AdminEnvironmentResultRequest request)
    {
        return new AdminEnvironmentStatusDto
        {
            Name = request.Target.Name,
            Url = request.Target.Url,
            Status = request.Status,
            Latency = request.Latency,
            Uptime = request.Uptime,
            Mode = request.Mode,
            LastCheckedUtc = request.LastCheckedUtc,
            StatusCode = request.StatusCode,
            Detail = request.Detail
        };
    }

    private static AdminEnvironmentOptions Normalize(AdminEnvironmentOptions options)
    {
        return new AdminEnvironmentOptions
        {
            SlowThresholdMilliseconds = Math.Clamp(options.SlowThresholdMilliseconds, 100, 30000),
            TimeoutMilliseconds = Math.Clamp(options.TimeoutMilliseconds, 1000, 60000),
            Targets = options.Targets
                .Where(target => !string.IsNullOrWhiteSpace(target.Name) && !string.IsNullOrWhiteSpace(target.Url))
                .ToArray()
        };
    }

    private sealed record AdminEnvironmentResultRequest
    {
        public required AdminEnvironmentTargetOptions Target { get; init; }
        public required string Status { get; init; }
        public required string Latency { get; init; }
        public required string Mode { get; init; }
        public required string Uptime { get; init; }
        public required DateTime LastCheckedUtc { get; init; }
        public required int StatusCode { get; init; }
        public required string Detail { get; init; }
    }
}
