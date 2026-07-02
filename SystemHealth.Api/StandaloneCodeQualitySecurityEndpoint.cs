using CRM.Application.SystemHealth;

sealed class StandaloneCodeQualitySecurityEndpoint
{
    private const string ApplicationKey = "my-life-story-vault";
    private const string ApplicationLabel = "My Life Story Vault";
    private const string DevelopmentEnvironment = "Development";
    private readonly SystemHealthOptions _options;
    private readonly CRM.Application.SystemHealth.CodeQualitySecurityService _service;

    public StandaloneCodeQualitySecurityEndpoint(
        SystemHealthOptions options,
        CRM.Application.SystemHealth.CodeQualitySecurityService service)
    {
        _options = options;
        _service = service;
    }

    public async Task<IResult> GetAsync(string? application, string? applicationKey, string? environment, CancellationToken cancellationToken)
    {
        var selectedApplication = string.IsNullOrWhiteSpace(applicationKey) ? application : applicationKey;
        var snapshot = await _service.GetSnapshotAsync(
            CreateCodeQualityOptions(),
            string.IsNullOrWhiteSpace(selectedApplication) ? ApplicationKey : selectedApplication,
            string.IsNullOrWhiteSpace(environment) ? DevelopmentEnvironment : environment,
            cancellationToken,
            CreateJenkinsOptions());

        return Results.Json(snapshot);
    }

    private CodeQualitySecurityOptions CreateCodeQualityOptions()
    {
        return new CodeQualitySecurityOptions
        {
            SonarBaseUrl = NormalizeSonarApiBaseUrl(_options.SonarQube.BaseUrl),
            JenkinsWorkspaceRoot = _options.CodeQualitySecurity.JenkinsWorkspaceRoot,
            JenkinsHomeRoot = _options.CodeQualitySecurity.JenkinsHomeRoot,
            SystemHealthArtifactRoot = _options.CodeQualitySecurity.SystemHealthArtifactRoot,
            DependencyCheckToolPath = _options.CodeQualitySecurity.DependencyCheckToolPath,
            GitHubToken = FirstConfigured(_options.Repository.GitHubToken, _options.CodeQualitySecurity.GitHubToken),
            GitHubOrganization = _options.Repository.Owner,
            GitHubRestApiUrl = _options.Repository.GitHubApiBaseUrl,
            GitHubGraphQlUrl = _options.CodeQualitySecurity.GitHubGraphQlUrl,
            GitHubDashboardUrl = _options.CodeQualitySecurity.GitHubDashboardUrl,
            SnapshotCacheSeconds = _options.CodeQualitySecurity.SnapshotCacheSeconds,
            Applications =
            [
                new CodeQualityApplicationOptions
                {
                    Key = ApplicationKey,
                    Label = ApplicationLabel,
                    GitHubOrganization = _options.Repository.Owner,
                    DevSonarProject = _options.SonarQube.ProjectKey,
                    ProdSonarProject = _options.SonarQube.ProjectKey,
                    DevGitHubRepository = _options.Repository.Name,
                    ProdGitHubRepository = _options.Repository.Name,
                    DevGitHubRef = _options.Repository.Ref,
                    ProdGitHubRef = _options.Repository.Ref,
                    DevDependencyCheckReportPath = _options.Artifacts.DependencyCheckReportPath,
                    ProdDependencyCheckReportPath = _options.Artifacts.DependencyCheckReportPath,
                    DevCycloneDxBomPath = _options.Artifacts.CycloneDxBomPath,
                    ProdCycloneDxBomPath = _options.Artifacts.CycloneDxBomPath,
                    DevPlaywrightReportPath = _options.Artifacts.PlaywrightReportPath,
                    ProdPlaywrightReportPath = _options.Artifacts.PlaywrightReportPath,
                    DevLintReportPath = _options.Artifacts.LintReportPath,
                    ProdLintReportPath = _options.Artifacts.LintReportPath,
                    DependencyCheckExpectedProjectName = _options.CodeQualitySecurity.DependencyCheckExpectedProjectName,
                    DependencyCheckExpectedScanFiles = _options.CodeQualitySecurity.DependencyCheckExpectedScanFiles,
                    DevDependencyCheckExpectedScanFiles = _options.CodeQualitySecurity.DependencyCheckExpectedScanFiles,
                    ProdDependencyCheckExpectedScanFiles = _options.CodeQualitySecurity.DependencyCheckExpectedScanFiles,
                    DependencyCheckMinimumDependenciesScanned = _options.CodeQualitySecurity.DependencyCheckMinimumDependenciesScanned,
                    DependencyCheckMaximumReportAgeHours = _options.CodeQualitySecurity.DependencyCheckMaximumReportAgeHours,
                    DependencyCheckRequirePackageLock = _options.CodeQualitySecurity.DependencyCheckRequirePackageLock,
                    DependencyCheckRequireNodeModules = _options.CodeQualitySecurity.DependencyCheckRequireNodeModules,
                    PlaywrightMaximumReportAgeHours = _options.CodeQualitySecurity.PlaywrightMaximumReportAgeHours,
                    LintMaximumReportAgeHours = _options.CodeQualitySecurity.LintMaximumReportAgeHours
                }
            ]
        };
    }

    private BuildDeploymentJenkinsOptions CreateJenkinsOptions()
    {
        return new BuildDeploymentJenkinsOptions
        {
            BaseUrl = _options.Jenkins.BaseUrl,
            Credentials = BuildJenkinsCredentials(_options.Jenkins.UserName, _options.Jenkins.ApiToken),
            Jobs =
            [
                new BuildDeploymentJenkinsJobOptions
                {
                    Key = ApplicationKey,
                    Label = ApplicationLabel,
                    DevJobName = _options.Jenkins.JobName,
                    ProdJobName = _options.Jenkins.JobName
                }
            ]
        };
    }

    private static string NormalizeSonarApiBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed}/api";
    }

    private static string BuildJenkinsCredentials(string userName, string apiToken)
    {
        return string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(apiToken)
            ? string.Empty
            : $"{userName}:{apiToken}";
    }

    private static string FirstConfigured(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
