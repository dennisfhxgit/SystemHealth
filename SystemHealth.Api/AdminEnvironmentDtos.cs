namespace SystemHealth.Api;

internal sealed class AdminEnvironmentSnapshotDto
{
    public DateTime GeneratedAtUtc { get; set; }
    public string Status { get; set; } = "Healthy";
    public string StatusDetail { get; set; } = string.Empty;
    public IReadOnlyList<AdminEnvironmentStatusDto> Environments { get; set; } = Array.Empty<AdminEnvironmentStatusDto>();
}

internal sealed class AdminEnvironmentStatusDto
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Latency { get; set; } = string.Empty;
    public string Uptime { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public DateTime LastCheckedUtc { get; set; }
    public int StatusCode { get; set; }
    public string Detail { get; set; } = string.Empty;
}

internal sealed class AdminEnvironmentOptions
{
    public int SlowThresholdMilliseconds { get; set; } = 1000;
    public int TimeoutMilliseconds { get; set; } = 5000;
    public AdminEnvironmentTargetOptions[] Targets { get; set; } = Array.Empty<AdminEnvironmentTargetOptions>();
}

internal sealed class AdminEnvironmentTargetOptions
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string UptimeAppPoolName { get; set; } = string.Empty;
}
