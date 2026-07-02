using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

builder.Services.AddSingleton<SystemHealthOptions>(sp =>
{
    var options = new SystemHealthOptions();
    sp.GetRequiredService<IConfiguration>().GetSection("SystemHealth").Bind(options);
    return options;
});
builder.Services.AddHttpClient<JenkinsTestResultsReader>();
builder.Services.AddSingleton<SystemHealthSnapshots>();
builder.Services.AddSingleton<StandaloneCodeQualitySecurityEndpoint>();
builder.Services.AddHttpClient<CRM.Application.SystemHealth.CodeQualitySecurityService>();
builder.Services.AddHttpClient("github", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("SystemHealth-Test12/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
});
builder.Services.AddHttpClient("sonarqube");

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api/system-health");
api.MapGet("/code-quality-security", (StandaloneCodeQualitySecurityEndpoint endpoint, string? application, string? applicationKey, string? environment, CancellationToken cancellationToken) => endpoint.GetAsync(application, applicationKey, environment, cancellationToken));
api.MapGet("/jenkins-log", (SystemHealthSnapshots snapshots) => snapshots.JenkinsLog());
api.MapGet("/test-results", (SystemHealthSnapshots snapshots, string? application, string? applicationKey, string? environment, string? buildId, CancellationToken cancellationToken) => snapshots.TestResultsAsync(application ?? applicationKey, environment, buildId, cancellationToken));
api.MapGet("/ai-code-analysis", (SystemHealthSnapshots snapshots) => snapshots.AiCodeAnalysis());
api.MapGet("/system-alerts", () => Results.Json(SystemHealthSnapshots.SystemAlerts()));
api.MapGet("/admin-environment", (SystemHealthOptions options) => Results.Json(SystemHealthSnapshots.AdminEnvironment(options)));
api.MapGet("/email-workers", () => Results.Json(SystemHealthSnapshots.EmailWorkers()));
api.MapGet("/artifact-history", (SystemHealthSnapshots snapshots, int? buildCount) => snapshots.ArtifactHistory(buildCount ?? 1));
api.MapGet("/backups", () => Results.Json(SystemHealthSnapshots.Unavailable("Backups", "Backups are not configured for the standalone Test12 SystemHealth app.")));
api.MapGet("/critical-events", () => Results.Json(new { sections = Array.Empty<CriticalHealthSection>() }));
api.MapPost("/critical-events", (CriticalHealthEventRequest request) =>
{
    var sections = request.Sections.Where(section => IsCritical(section.Status)).ToArray();
    return Results.Json(new
    {
        critical = sections.Length > 0,
        notificationSent = false,
        duplicateSuppressed = false,
        sections
    });
});
api.MapGet("/critical-events/history", () => Results.Json(Array.Empty<object>()));
api.MapPost("/critical-events/history/{id:int}/acknowledge", (int id) => Results.Json(new { acknowledged = false, id, detail = "Critical alert acknowledgement persistence is disabled in the standalone Test12 app." }));

app.MapFallbackToFile("index.html");
app.MapGet("/health", () => Results.Json(new { status = "OK", app = "SystemHealth", repository = "MyLifeStoryVault-Ltd/My-Life-Story-Vault" }));

app.Run();

static bool IsCritical(string? status)
{
    return string.Equals(status, "Critical", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "Failure", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase);
}

sealed class SystemHealthSnapshots
{
    private const string ApplicationKey = "my-life-story-vault";
    private const string ApplicationLabel = "My Life Story Vault";
    private const string EnvironmentName = "Development";
    private readonly SystemHealthOptions _options;
    private readonly JenkinsTestResultsReader _testResultsReader;

    public SystemHealthSnapshots(SystemHealthOptions options, JenkinsTestResultsReader testResultsReader)
    {
        _options = options;
        _testResultsReader = testResultsReader;
    }

    public IResult JenkinsLog()
    {
        return Results.Json(new
        {
            status = "Warning",
            statusDetail = "Jenkins log access is not configured in source; configure Test12 runtime Jenkins credentials to enable live logs.",
            selectedApplicationKey = ApplicationKey,
            selectedEnvironment = EnvironmentName,
            applications = Applications(),
            environments = Environments(),
            jobName = _options.Jenkins.JobName,
            buildId = "Last Build",
            buildStatus = "Unavailable",
            logText = ""
        });
    }

    public async Task<IResult> TestResultsAsync(string? applicationKey, string? environment, string? buildId, CancellationToken cancellationToken)
    {
        return Results.Json(await _testResultsReader.GetAsync(_options, applicationKey, environment, buildId, cancellationToken));
    }

    public IResult AiCodeAnalysis()
    {
        var artifactConfigured = IsConfigured(_options.Artifacts.AiCodeAnalysisPath);
        return Results.Json(new
        {
            status = artifactConfigured ? "Warning" : "Warning",
            statusDetail = artifactConfigured ? "AI code analysis artifact path is configured; live parsing is not enabled until Test12 runtime artifacts are present." : "ai-code-analysis.json was not found because the artifact path is not configured.",
            selectedApplicationKey = ApplicationKey,
            selectedEnvironment = EnvironmentName,
            applications = Applications(),
            environments = Environments(),
            providerName = "Jenkins archived AI analysis artifact",
            jobName = _options.Jenkins.JobName,
            buildId = "Last Build",
            findings = Array.Empty<object>(),
            checks = new[]
            {
                new { name = "Artifact Path", category = "Runtime", status = artifactConfigured ? "Warning" : "Unavailable", detail = artifactConfigured ? "Path configured; artifact must be present at runtime." : "ai-code-analysis.json was not found because no artifact path is configured." },
                new { name = "Repository Scope", category = "Source", status = "Healthy", detail = $"{_options.Repository.Owner}/{_options.Repository.Name}" }
            },
            severityCounts = SeverityCounts()
        });
    }

    public IResult ArtifactHistory(int buildCount)
    {
        return Results.Json(new
        {
            status = "Warning",
            statusDetail = "Jenkins artifact history is not configured in source.",
            selectedApplicationKey = ApplicationKey,
            selectedEnvironment = EnvironmentName,
            applications = Applications(),
            environments = Environments(),
            jobName = _options.Jenkins.JobName,
            selectedBuildCount = buildCount,
            buildCounts = new[] { 1, 10, 30, 50, 100 },
            artifacts = Array.Empty<object>()
        });
    }

    public static object SystemAlerts()
    {
        return new
        {
            status = "Warning",
            statusDetail = "Standalone Test12 SystemHealth has no MSSQL or source database health check.",
            summary = new
            {
                critical = 0,
                warnings = 1,
                processCpuPercent = 0,
                memoryUsagePercent = 0,
                dataServerCpuPercent = 0,
                dataServerMemoryUsagePercent = 0
            },
            applicationDrives = Array.Empty<object>(),
            dataServerDrives = Array.Empty<object>(),
            checks = new[]
            {
                new { name = "MSSQL dependency", status = "Healthy", detail = "No MSSQL connection is required.", lastCheckedUtc = DateTimeOffset.UtcNow },
                new { name = "Access gate dependency", status = "Healthy", detail = "No interactive access gate or database-backed user state is required.", lastCheckedUtc = DateTimeOffset.UtcNow }
            },
            dataServerChecks = Array.Empty<object>(),
            alerts = new[]
            {
                new { source = "Runtime Configuration", status = "Warning", severity = "Warning", detail = "External provider credentials are runtime configuration and are not committed to source.", detectedAtUtc = DateTimeOffset.UtcNow }
            }
        };
    }

    public static object AdminEnvironment(SystemHealthOptions options)
    {
        return new
        {
            status = "Warning",
            statusDetail = "Test12 target route is configured as a source value; live target verification must be run after deployment.",
            environments = new[]
            {
                new
                {
                    name = "Test12",
                    url = options.Test12BaseRoute,
                    status = "Warning",
                    latency = "Not measured",
                    uptime = "Not measured",
                    mode = "Read-only",
                    lastCheckedUtc = DateTimeOffset.UtcNow,
                    detail = $"Reports only on {options.Repository.Owner}/{options.Repository.Name}."
                }
            }
        };
    }

    public static object EmailWorkers()
    {
        return new
        {
            overallStatus = "Unavailable",
            statusDetail = "Email worker database state is intentionally removed from the standalone Test12 app.",
            workers = Array.Empty<object>()
        };
    }

    public static object Unavailable(string section, string detail)
    {
        return new { status = "Unavailable", statusDetail = detail, section };
    }

    private static object[] Applications() => new[] { new { key = ApplicationKey, label = ApplicationLabel } };
    private static string[] Environments() => new[] { EnvironmentName };
    private static object[] SeverityCounts() => new[] { new { severity = "Critical", count = 0 }, new { severity = "High", count = 0 }, new { severity = "Medium", count = 0 }, new { severity = "Low", count = 0 } };
    private static bool IsConfigured(string? value) => !string.IsNullOrWhiteSpace(value);
}

sealed class SystemHealthOptions
{
    public string Test12BaseRoute { get; set; } = "/";
    public string ApiBaseUrl { get; set; } = "/api";
    public RepositoryOptions Repository { get; set; } = new();
    public JenkinsOptions Jenkins { get; set; } = new();
    public SonarQubeOptions SonarQube { get; set; } = new();
    public ArtifactOptions Artifacts { get; set; } = new();
    public CodeQualitySecurityRuntimeOptions CodeQualitySecurity { get; set; } = new();
}

sealed class RepositoryOptions
{
    public string Owner { get; set; } = "MyLifeStoryVault-Ltd";
    public string Name { get; set; } = "My-Life-Story-Vault";
    public string Url { get; set; } = "https://github.com/MyLifeStoryVault-Ltd/My-Life-Story-Vault";
    public string Ref { get; set; } = "main";
    public string GitHubApiBaseUrl { get; set; } = "https://api.github.com";
    public string GitHubToken { get; set; } = string.Empty;
}

sealed class JenkinsOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string JobName { get; set; } = "SystemHealth";
    public string UserName { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
}

sealed class SonarQubeOptions
{
    public string BaseUrl { get; set; } = "https://sonarqube.fhx.co.nz";
    public string ProjectKey { get; set; } = "My-Life-Story-Vault";
    public string Token { get; set; } = string.Empty;
}

sealed class ArtifactOptions
{
    public string DependencyCheckReportPath { get; set; } = string.Empty;
    public string LintReportPath { get; set; } = string.Empty;
    public string CycloneDxBomPath { get; set; } = string.Empty;
    public string PlaywrightReportPath { get; set; } = string.Empty;
    public string AiCodeAnalysisPath { get; set; } = string.Empty;
}

sealed class CodeQualitySecurityRuntimeOptions
{
    public string GitHubToken { get; set; } = string.Empty;
    public string JenkinsWorkspaceRoot { get; set; } = @"C:\ProgramData\Jenkins\.jenkins\workspace";
    public string JenkinsHomeRoot { get; set; } = @"C:\ProgramData\Jenkins\.jenkins";
    public string SystemHealthArtifactRoot { get; set; } = @"C:\ProgramData\Jenkins\.jenkins\fhx-system-health";
    public string DependencyCheckToolPath { get; set; } = @"C:\OWASP\DependencyCheck\bin\dependency-check.bat";
    public string GitHubGraphQlUrl { get; set; } = "https://api.github.com/graphql";
    public string GitHubDashboardUrl { get; set; } = "https://github.com/orgs/MyLifeStoryVault-Ltd/security/overview";
    public int SnapshotCacheSeconds { get; set; } = 120;
    public string DependencyCheckExpectedProjectName { get; set; } = "My-Life-Story-Vault";
    public string[] DependencyCheckExpectedScanFiles { get; set; } = Array.Empty<string>();
    public int DependencyCheckMinimumDependenciesScanned { get; set; } = 20;
    public int DependencyCheckMaximumReportAgeHours { get; set; } = 48;
    public bool DependencyCheckRequirePackageLock { get; set; } = true;
    public bool DependencyCheckRequireNodeModules { get; set; }
    public int PlaywrightMaximumReportAgeHours { get; set; } = 24;
    public int LintMaximumReportAgeHours { get; set; } = 24;
}

sealed class CriticalHealthEventRequest
{
    public CriticalHealthSection[] Sections { get; set; } = Array.Empty<CriticalHealthSection>();
}

sealed class CriticalHealthSection
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}
