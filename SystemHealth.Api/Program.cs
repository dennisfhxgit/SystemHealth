using System.Text.Json;
using System.Text.Json.Nodes;
using SystemHealth.Api;

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
builder.Services.AddHttpClient<JenkinsLogReader>();
builder.Services.AddHttpClient<JenkinsArtifactHistoryReader>();
builder.Services.AddHttpClient<JenkinsAiCodeAnalysisReader>();
builder.Services.AddHttpClient<StandaloneSystemAlertsReader>();
builder.Services.AddHttpClient<AdminEnvironmentHealthService>();
builder.Services.AddSingleton<StandaloneEmailWorkersReader>();
builder.Services.AddSingleton<IAdminEnvironmentUptimeProvider, IisAdminEnvironmentUptimeProvider>();
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
api.MapGet("/jenkins-log", (SystemHealthSnapshots snapshots, string? application, string? applicationKey, string? environment, string? buildId, CancellationToken cancellationToken) => snapshots.JenkinsLogAsync(application ?? applicationKey, environment, buildId, cancellationToken));
api.MapGet("/test-results", (SystemHealthSnapshots snapshots, string? application, string? applicationKey, string? environment, string? buildId, CancellationToken cancellationToken) => snapshots.TestResultsAsync(application ?? applicationKey, environment, buildId, cancellationToken));
api.MapGet("/ai-code-analysis", async (JenkinsAiCodeAnalysisReader reader, SystemHealthOptions options, string? application, string? applicationKey, string? environment, string? buildId, CancellationToken cancellationToken) =>
    Results.Json(await reader.GetAsync(options, application ?? applicationKey, environment, buildId, cancellationToken)));
api.MapGet("/system-alerts", async (StandaloneSystemAlertsReader reader, CancellationToken cancellationToken) => Results.Json(await reader.GetAsync(cancellationToken)));
api.MapGet("/admin-environment", async (AdminEnvironmentHealthService service, CancellationToken cancellationToken) => Results.Json(await service.GetSnapshotAsync(cancellationToken)));
api.MapGet("/email-workers", async (StandaloneEmailWorkersReader reader, CancellationToken cancellationToken) => Results.Json(await reader.GetAsync(cancellationToken)));
api.MapGet("/artifact-history", (SystemHealthSnapshots snapshots, string? application, string? applicationKey, string? environment, int? buildCount, CancellationToken cancellationToken) => snapshots.ArtifactHistoryAsync(application ?? applicationKey, environment, buildCount, cancellationToken));
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
    private readonly JenkinsLogReader _jenkinsLogReader;
    private readonly JenkinsArtifactHistoryReader _artifactHistoryReader;

    public SystemHealthSnapshots(SystemHealthOptions options, JenkinsTestResultsReader testResultsReader, JenkinsLogReader jenkinsLogReader, JenkinsArtifactHistoryReader artifactHistoryReader)
    {
        _options = options;
        _testResultsReader = testResultsReader;
        _jenkinsLogReader = jenkinsLogReader;
        _artifactHistoryReader = artifactHistoryReader;
    }

    public async Task<IResult> JenkinsLogAsync(string? applicationKey, string? environment, string? buildId, CancellationToken cancellationToken)
    {
        return Results.Json(await _jenkinsLogReader.GetAsync(_options, applicationKey, environment, buildId, cancellationToken));
    }

    public async Task<IResult> TestResultsAsync(string? applicationKey, string? environment, string? buildId, CancellationToken cancellationToken)
    {
        return Results.Json(await _testResultsReader.GetAsync(_options, applicationKey, environment, buildId, cancellationToken));
    }

    public async Task<IResult> ArtifactHistoryAsync(string? applicationKey, string? environment, int? buildCount, CancellationToken cancellationToken)
    {
        return Results.Json(await _artifactHistoryReader.GetAsync(_options, applicationKey, environment, buildCount, cancellationToken));
    }

    public static object Unavailable(string section, string detail)
    {
        return new { status = "Unavailable", statusDetail = detail, section };
    }

    private static object[] Applications() => new[] { new { key = ApplicationKey, label = ApplicationLabel } };
    private static string[] Environments() => new[] { EnvironmentName };
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
    public SystemAlertsOptions SystemAlerts { get; set; } = new();
    public AdminEnvironmentOptions AdminEnvironment { get; set; } = new();
    public EmailWorkersOptions EmailWorkers { get; set; } = new();
}

sealed class RepositoryOptions
{
    public string Owner { get; set; } = "MyLifeStoryVault-Ltd";
    public string Name { get; set; } = "My-Life-Story-Vault";
    public string Url { get; set; } = "https://github.com/MyLifeStoryVault-Ltd/My-Life-Story-Vault";
    public string Ref { get; set; } = "master";
    public string GitHubApiBaseUrl { get; set; } = "https://api.github.com";
    public string GitHubToken { get; set; } = string.Empty;
}

sealed class JenkinsOptions
{
    public string BaseUrl { get; set; } = "https://jenkins.fhx.co.nz";
    public string JobName { get; set; } = "SystemHealth";
    public string UserName { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public int MaxLogCharacters { get; set; } = 60000;
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

sealed class SystemAlertsOptions
{
    public int DiskWarningPercent { get; set; } = 85;
    public int DiskCriticalPercent { get; set; } = 95;
    public int MemoryWarningPercent { get; set; } = 85;
    public int MemoryCriticalPercent { get; set; } = 95;
    public int ProcessCpuWarningPercent { get; set; } = 70;
    public int ProcessCpuCriticalPercent { get; set; } = 90;
    public string DeploymentRootPath { get; set; } = string.Empty;
    public string[] ApplicationServerDriveLetters { get; set; } = ["B:\\", "C:\\", "W:\\"];
    public string[] DataServerDriveLetters { get; set; } = ["C:\\", "D:\\", "L:\\"];
    public string ApplicationServerMetricsSnapshotPath { get; set; } = @"C:\ProgramData\FHX\SystemHealth\test11-application-server-metrics.json";
    public int ApplicationServerMetricsSnapshotMaxAgeMinutes { get; set; } = 30;
    public string DataServerMetricsUrl { get; set; } = string.Empty;
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
