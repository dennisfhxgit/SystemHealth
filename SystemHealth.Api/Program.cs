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
builder.Services.AddSingleton<SystemHealthSnapshots>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api/system-health");
api.MapGet("/code-quality-security", (SystemHealthSnapshots snapshots) => snapshots.CodeQualitySecurity());
api.MapGet("/jenkins-log", (SystemHealthSnapshots snapshots) => snapshots.JenkinsLog());
api.MapGet("/test-results", (SystemHealthSnapshots snapshots) => snapshots.TestResults());
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

    public SystemHealthSnapshots(SystemHealthOptions options)
    {
        _options = options;
    }

    public IResult CodeQualitySecurity()
    {
        var providerStatuses = new List<object>
        {
            ProviderStatus("GitHub", "Warning", "GitHub token/base integration is runtime configuration. No committed credential is required."),
            ProviderStatus("SonarQube", IsConfigured(_options.SonarQube.BaseUrl) ? "Warning" : "Warning", IsConfigured(_options.SonarQube.BaseUrl) ? "SonarQube can be queried when runtime credentials are configured." : "SonarQube base URL is not configured in source."),
            ProviderStatus("Jenkins", IsConfigured(_options.Jenkins.BaseUrl) ? "Warning" : "Warning", IsConfigured(_options.Jenkins.BaseUrl) ? "Jenkins can be queried when runtime credentials are configured." : "Jenkins base URL is not configured in source."),
            ProviderStatus("MSSQL", "Unavailable", "MSSQL is intentionally not used by the standalone Test12 SystemHealth app.")
        };

        return Results.Json(new
        {
            status = "Warning",
            statusDetail = "Provider credentials and artifacts must be supplied through Test12 runtime configuration; no MSSQL or login dependency is required.",
            generatedAtUtc = DateTimeOffset.UtcNow,
            selectedApplicationKey = ApplicationKey,
            selectedEnvironment = EnvironmentName,
            applications = Applications(),
            environments = Environments(),
            providerStatuses,
            sonarProjectKey = _options.SonarQube.ProjectKey,
            sonarMetrics = Array.Empty<object>(),
            vulnerabilities = Array.Empty<object>(),
            bugs = Array.Empty<object>(),
            codeSmells = Array.Empty<object>(),
            gitHubRepository = $"{_options.Repository.Owner}/{_options.Repository.Name}",
            gitHubSeverityCounts = SeverityCounts(),
            gitHubAlerts = Array.Empty<object>(),
            gitHubCodeScanningSeverityCounts = SeverityCounts(),
            gitHubCodeScanningAlerts = Array.Empty<object>(),
            gitHubSecretScanningCounts = new[] { new { key = "OPEN", label = "Open", count = 0 } },
            gitHubSecretScanningAlerts = Array.Empty<object>(),
            lintStatus = ArtifactStatus(_options.Artifacts.LintReportPath),
            lintStatusDetail = ArtifactDetail(_options.Artifacts.LintReportPath, "Lint & Standards"),
            lintErrorCount = 0,
            lintWarningCount = 0,
            lintToolsTotal = 0,
            lintToolsPassed = 0,
            lintToolsFailed = 0,
            lintToolsNotApplicable = 0,
            lintToolsNotConfigured = 0,
            lintFindings = Array.Empty<object>(),
            lintDisplayedCount = 0,
            lintTotalFindings = 0,
            dependencyCheckStatus = ArtifactStatus(_options.Artifacts.DependencyCheckReportPath),
            dependencyCheckStatusDetail = ArtifactDetail(_options.Artifacts.DependencyCheckReportPath, "OWASP Dependency-Check"),
            dependencyCheckSeverityCounts = SeverityCounts(),
            dependencyCheckVulnerabilityCount = 0,
            dependencyCheckFindings = Array.Empty<object>(),
            cycloneDxStatus = "Unavailable",
            cycloneDxStatusDetail = "CycloneDX SBOM artifact path is not configured.",
            cycloneDxComponents = Array.Empty<object>(),
            cycloneDxComponentCount = 0,
            playwrightStatus = ArtifactStatus(_options.Artifacts.PlaywrightReportPath),
            playwrightStatusDetail = ArtifactDetail(_options.Artifacts.PlaywrightReportPath, "Playwright"),
            playwrightTotalTests = 0,
            playwrightPassedTests = 0,
            playwrightFailedTests = 0,
            playwrightSkippedTests = 0,
            playwrightResults = Array.Empty<object>(),
            playwrightWorkflowContracts = Array.Empty<object>()
        });
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

    public IResult TestResults()
    {
        return Results.Json(new
        {
            status = "Warning",
            statusDetail = "Jenkins test-report access is not configured in source.",
            selectedApplicationKey = ApplicationKey,
            selectedEnvironment = EnvironmentName,
            applications = Applications(),
            environments = Environments(),
            jobName = _options.Jenkins.JobName,
            buildId = "Last Build",
            totalTests = 0,
            passedTests = 0,
            failedTests = 0,
            skippedTests = 0,
            apiFunctionalResults = Array.Empty<object>(),
            apiPerformanceResults = Array.Empty<object>(),
            uiTestResults = Array.Empty<object>()
        });
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
    private static object ProviderStatus(string provider, string status, string detail) => new { provider, status, detail };
    private static bool IsConfigured(string? value) => !string.IsNullOrWhiteSpace(value);
    private static string ArtifactStatus(string? path) => IsConfigured(path) ? File.Exists(path) ? "Healthy" : "Warning" : "Unavailable";
    private static string ArtifactDetail(string? path, string name)
    {
        if (!IsConfigured(path))
        {
            return $"{name} artifact path is not configured.";
        }

        return File.Exists(path) ? $"{name} artifact exists at the configured runtime path." : $"{name} artifact was not found at the configured runtime path.";
    }
}

sealed class SystemHealthOptions
{
    public string Test12BaseRoute { get; set; } = "/";
    public string ApiBaseUrl { get; set; } = "/api";
    public RepositoryOptions Repository { get; set; } = new();
    public JenkinsOptions Jenkins { get; set; } = new();
    public SonarQubeOptions SonarQube { get; set; } = new();
    public ArtifactOptions Artifacts { get; set; } = new();
}

sealed class RepositoryOptions
{
    public string Owner { get; set; } = "MyLifeStoryVault-Ltd";
    public string Name { get; set; } = "My-Life-Story-Vault";
    public string Url { get; set; } = "https://github.com/MyLifeStoryVault-Ltd/My-Life-Story-Vault";
    public string Ref { get; set; } = "main";
}

sealed class JenkinsOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string JobName { get; set; } = "SystemHealth";
}

sealed class SonarQubeOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ProjectKey { get; set; } = "My-Life-Story-Vault";
}

sealed class ArtifactOptions
{
    public string DependencyCheckReportPath { get; set; } = string.Empty;
    public string LintReportPath { get; set; } = string.Empty;
    public string PlaywrightReportPath { get; set; } = string.Empty;
    public string AiCodeAnalysisPath { get; set; } = string.Empty;
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
