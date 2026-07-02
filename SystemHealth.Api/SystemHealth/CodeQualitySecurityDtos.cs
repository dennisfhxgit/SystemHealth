namespace CRM.Application.SystemHealth;

public sealed class CodeQualitySecuritySnapshotDto
{
    public DateTime GeneratedAtUtc { get; set; }
    public string Status { get; set; } = "Healthy";
    public string StatusDetail { get; set; } = string.Empty;
    public string SelectedApplicationKey { get; set; } = string.Empty;
    public string SelectedEnvironment { get; set; } = string.Empty;
    public string SonarComponent { get; set; } = string.Empty;
    public DateTime? SonarAnalysisDateUtc { get; set; }
    public string SonarAnalysisRevision { get; set; } = string.Empty;
    public string SonarAnalysisVersion { get; set; } = string.Empty;
    public string GitHubRepository { get; set; } = string.Empty;
    public string GitHubCodeScanningRef { get; set; } = string.Empty;
    public string GitHubCodeScanningHeadCommitSha { get; set; } = string.Empty;
    public string GitHubCodeScanningAnalysisCommitSha { get; set; } = string.Empty;
    public DateTime? GitHubCodeScanningAnalysisDateUtc { get; set; }
    public string GitHubCodeScanningAnalysisKey { get; set; } = string.Empty;
    public string GitHubCodeScanningAnalysisCategory { get; set; } = string.Empty;
    public bool GitHubCodeScanningIsCurrent { get; set; }
    public bool GitHubCodeScanningIsAnalysisPending { get; set; }
    public string GitHubCodeScanningAnalysisRunUrl { get; set; } = string.Empty;
    public string GitHubCodeScanningFreshnessDetail { get; set; } = string.Empty;
    public string GitHubDashboardUrl { get; set; } = string.Empty;
    public IReadOnlyList<CodeQualityApplicationOptionDto> Applications { get; set; } = Array.Empty<CodeQualityApplicationOptionDto>();
    public IReadOnlyList<string> Environments { get; set; } = Array.Empty<string>();
    public IReadOnlyList<SonarMetricDto> SonarMetrics { get; set; } = Array.Empty<SonarMetricDto>();
    public IReadOnlyList<SonarIssueDto> CodeSmells { get; set; } = Array.Empty<SonarIssueDto>();
    public int CodeSmellTotalCount { get; set; }
    public int CodeSmellDisplayedCount { get; set; }
    public bool CodeSmellDetailsTruncated { get; set; }
    public IReadOnlyList<SonarIssueDto> Vulnerabilities { get; set; } = Array.Empty<SonarIssueDto>();
    public int VulnerabilityTotalCount { get; set; }
    public int VulnerabilityDisplayedCount { get; set; }
    public bool VulnerabilityDetailsTruncated { get; set; }
    public IReadOnlyList<SonarIssueDto> Bugs { get; set; } = Array.Empty<SonarIssueDto>();
    public int BugTotalCount { get; set; }
    public int BugDisplayedCount { get; set; }
    public bool BugDetailsTruncated { get; set; }
    public IReadOnlyList<GitHubSeverityCountDto> GitHubSeverityCounts { get; set; } = Array.Empty<GitHubSeverityCountDto>();
    public IReadOnlyList<GitHubSecurityAlertDto> GitHubAlerts { get; set; } = Array.Empty<GitHubSecurityAlertDto>();
    public IReadOnlyList<GitHubSeverityCountDto> GitHubCodeScanningSeverityCounts { get; set; } = Array.Empty<GitHubSeverityCountDto>();
    public IReadOnlyList<GitHubCodeScanningAlertDto> GitHubCodeScanningAlerts { get; set; } = Array.Empty<GitHubCodeScanningAlertDto>();
    public IReadOnlyList<GitHubSecretScanningCountDto> GitHubSecretScanningCounts { get; set; } = Array.Empty<GitHubSecretScanningCountDto>();
    public IReadOnlyList<GitHubSecretScanningAlertDto> GitHubSecretScanningAlerts { get; set; } = Array.Empty<GitHubSecretScanningAlertDto>();
    public string DependencyCheckStatus { get; set; } = string.Empty;
    public string DependencyCheckStatusDetail { get; set; } = string.Empty;
    public string DependencyCheckProjectName { get; set; } = string.Empty;
    public string DependencyCheckEngineVersion { get; set; } = string.Empty;
    public string DependencyCheckReportPath { get; set; } = string.Empty;
    public DateTime? DependencyCheckReportDateUtc { get; set; }
    public int DependencyCheckDependenciesScanned { get; set; }
    public int DependencyCheckVulnerabilityCount { get; set; }
    public IReadOnlyList<DependencyCheckSeverityCountDto> DependencyCheckSeverityCounts { get; set; } = Array.Empty<DependencyCheckSeverityCountDto>();
    public IReadOnlyList<DependencyCheckFindingDto> DependencyCheckFindings { get; set; } = Array.Empty<DependencyCheckFindingDto>();
    public string CycloneDxStatus { get; set; } = string.Empty;
    public string CycloneDxStatusDetail { get; set; } = string.Empty;
    public string CycloneDxBomPath { get; set; } = string.Empty;
    public string CycloneDxBomFormat { get; set; } = string.Empty;
    public string CycloneDxSpecVersion { get; set; } = string.Empty;
    public string CycloneDxSerialNumber { get; set; } = string.Empty;
    public DateTime? CycloneDxGeneratedAtUtc { get; set; }
    public int CycloneDxComponentCount { get; set; }
    public IReadOnlyList<CycloneDxComponentDto> CycloneDxComponents { get; set; } = Array.Empty<CycloneDxComponentDto>();
    public string LintStatus { get; set; } = string.Empty;
    public string LintStatusDetail { get; set; } = string.Empty;
    public DateTime? LintGeneratedAtUtc { get; set; }
    public int LintTotalFindings { get; set; }
    public int LintDisplayedCount { get; set; }
    public int LintErrorCount { get; set; }
    public int LintWarningCount { get; set; }
    public int LintToolsTotal { get; set; }
    public int LintToolsPassed { get; set; }
    public int LintToolsFailed { get; set; }
    public int LintToolsNotApplicable { get; set; }
    public int LintToolsNotConfigured { get; set; }
    public IReadOnlyList<LintToolResultDto> LintToolResults { get; set; } = Array.Empty<LintToolResultDto>();
    public IReadOnlyList<LintFindingDto> LintFindings { get; set; } = Array.Empty<LintFindingDto>();
    public string PlaywrightStatus { get; set; } = string.Empty;
    public string PlaywrightStatusDetail { get; set; } = string.Empty;
    public string PlaywrightProjectName { get; set; } = string.Empty;
    public string PlaywrightBaseUrl { get; set; } = string.Empty;
    public DateTime? PlaywrightGeneratedAtUtc { get; set; }
    public int PlaywrightTotalTests { get; set; }
    public int PlaywrightPassedTests { get; set; }
    public int PlaywrightFailedTests { get; set; }
    public int PlaywrightSkippedTests { get; set; }
    public double PlaywrightDurationSeconds { get; set; }
    public IReadOnlyList<PlaywrightResultDto> PlaywrightResults { get; set; } = Array.Empty<PlaywrightResultDto>();
    public IReadOnlyList<PlaywrightWorkflowContractDto> PlaywrightWorkflowContracts { get; set; } = Array.Empty<PlaywrightWorkflowContractDto>();
    public IReadOnlyList<CodeQualityProviderStatusDto> ProviderStatuses { get; set; } = Array.Empty<CodeQualityProviderStatusDto>();
}

public sealed class CodeQualityApplicationOptionDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public sealed class SonarMetricDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool BestValue { get; set; }
}

public sealed class SonarIssueDto
{
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Component { get; set; } = string.Empty;
    public int? Line { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public sealed class GitHubSeverityCountDto
{
    public string Severity { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class GitHubSecurityAlertDto
{
    public string Severity { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

public sealed class GitHubCodeScanningAlertDto
{
    public string Severity { get; set; } = string.Empty;
    public string RuleId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int? Line { get; set; }
    public string Ref { get; set; } = string.Empty;
    public string CommitSha { get; set; } = string.Empty;
    public DateTime? FixedAtUtc { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public sealed class GitHubSecretScanningCountDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class GitHubSecretScanningAlertDto
{
    public string SecretType { get; set; } = string.Empty;
    public string SecretTypeDisplayName { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public DateTime? CreatedAtUtc { get; set; }
    public string Url { get; set; } = string.Empty;
}

public sealed class DependencyCheckSeverityCountDto
{
    public string Severity { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class DependencyCheckFindingDto
{
    public string Severity { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
    public string Dependency { get; set; } = string.Empty;
    public string PackagePath { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public double? CvssScore { get; set; }
    public string Url { get; set; } = string.Empty;
}

public sealed class CycloneDxComponentDto
{
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string PackageUrl { get; set; } = string.Empty;
    public string BomRef { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
}

public sealed class LintToolResultDto
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Errors { get; set; }
    public int Warnings { get; set; }
    public string Detail { get; set; } = string.Empty;
    public int ExitCode { get; set; }
}

public sealed class LintFindingDto
{
    public string Tool { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string RuleId { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public int? Line { get; set; }
    public int? Column { get; set; }
    public string Message { get; set; } = string.Empty;
    public string RawSummary { get; set; } = string.Empty;
}

public sealed class PlaywrightResultDto
{
    public string Id { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public string Step { get; set; } = string.Empty;
    public string Browser { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public double Duration { get; set; }
    public string Screenshot { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

public sealed class PlaywrightWorkflowContractDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string DirectScenarioStatus { get; set; } = string.Empty;
    public string DropdownScenarioStatus { get; set; } = string.Empty;
    public string AuthenticatedScenarioStatus { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusDetail { get; set; } = string.Empty;
}

public sealed class CodeQualityProviderStatusDto
{
    public string Provider { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTime CheckedAtUtc { get; set; }
}

public sealed class CodeQualitySecurityOptions
{
    public string SonarBaseUrl { get; set; } = string.Empty;
    public string JenkinsWorkspaceRoot { get; set; } = @"C:\ProgramData\Jenkins\.jenkins\workspace";
    public string JenkinsHomeRoot { get; set; } = @"C:\ProgramData\Jenkins\.jenkins";
    public string SystemHealthArtifactRoot { get; set; } = @"C:\ProgramData\Jenkins\.jenkins\fhx-system-health";
    public string DependencyCheckToolPath { get; set; } = @"C:\OWASP\DependencyCheck\bin\dependency-check.bat";
    public string GitHubToken { get; set; } = string.Empty;
    public string GitHubOrganization { get; set; } = "MyLifeStoryVault-Ltd";
    public string GitHubGraphQlUrl { get; set; } = "https://api.github.com/graphql";
    public string GitHubRestApiUrl { get; set; } = "https://api.github.com";
    public string GitHubDashboardUrl { get; set; } = "https://github.com/orgs/MyLifeStoryVault-Ltd/security/overview";
    public int SnapshotCacheSeconds { get; set; } = 120;
    public CodeQualityApplicationOptions[] Applications { get; set; } = Array.Empty<CodeQualityApplicationOptions>();
}

public sealed class CodeQualityApplicationOptions
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string GitHubOrganization { get; set; } = string.Empty;
    public string SonarProject { get; set; } = string.Empty;
    public string ProdSonarProject { get; set; } = string.Empty;
    public string DevSonarProject { get; set; } = string.Empty;
    public string ProdGitHubRepository { get; set; } = string.Empty;
    public string DevGitHubRepository { get; set; } = string.Empty;
    public string GitHubRef { get; set; } = string.Empty;
    public string ProdGitHubRef { get; set; } = string.Empty;
    public string DevGitHubRef { get; set; } = string.Empty;
    public string ProdDependencyCheckReportPath { get; set; } = string.Empty;
    public string DevDependencyCheckReportPath { get; set; } = string.Empty;
    public string ProdCycloneDxBomPath { get; set; } = string.Empty;
    public string DevCycloneDxBomPath { get; set; } = string.Empty;
    public string DependencyCheckExpectedProjectName { get; set; } = string.Empty;
    public string[] DependencyCheckExpectedScanFiles { get; set; } = Array.Empty<string>();
    public string[] DevDependencyCheckExpectedScanFiles { get; set; } = Array.Empty<string>();
    public string[] ProdDependencyCheckExpectedScanFiles { get; set; } = Array.Empty<string>();
    public int DependencyCheckMinimumDependenciesScanned { get; set; }
    public int DependencyCheckMaximumReportAgeHours { get; set; }
    public bool DependencyCheckRequirePackageLock { get; set; }
    public bool DependencyCheckRequireNodeModules { get; set; }
    public string ProdPlaywrightReportPath { get; set; } = string.Empty;
    public string DevPlaywrightReportPath { get; set; } = string.Empty;
    public string ProdLintReportPath { get; set; } = string.Empty;
    public string DevLintReportPath { get; set; } = string.Empty;
    public int PlaywrightMaximumReportAgeHours { get; set; } = 24;
    public int LintMaximumReportAgeHours { get; set; } = 24;
}
