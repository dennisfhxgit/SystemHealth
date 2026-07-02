namespace CRM.Application.SystemHealth;

using System.Globalization;
using System.Text.Json;

public sealed class CodeQualitySecurityService
{
    private const string ProductionEnvironment = "Production";
    private const string DevelopmentEnvironment = "Development";
    private const string DefaultApplicationKey = "my-life-story-vault";
    private const string CriticalStatus = "Critical";
    private const string WarningStatus = "Warning";
    private const string HealthyStatus = "Healthy";
    private const string NotApplicableStatus = "NotApplicable";
    private const string NotConfiguredStatus = "NotConfigured";
    private const string UnavailableEnvironmentDetail = "This environment is not available.";
    private const string CriticalSeverity = "CRITICAL";
    private const string ProviderSuccessStatus = "Success";
    private const string ProviderUnavailableStatus = "Unavailable";
    private const string ProviderRateLimitedStatus = "RateLimited";
    private const string DependencyCheckProviderName = "OWASP Dependency-Check";
    private const string CycloneDxProviderName = "CycloneDX SBOM";
    private const string PlaywrightProviderName = "Playwright";
    private const string LintProviderName = "Lint & Standards";
    private const string StatusJsonProperty = "status";
    private const string SonarProviderName = "SonarQube";
    private const string GitHubCodeQlProviderName = "GitHub CodeQL";
    private const string JenkinsArtifactRootDirectory = "_jenkins";
    private const int MaximumDependencyCheckFindings = 100;
    private const int MaximumCycloneDxComponents = 100;
    private const int MaximumPlaywrightResults = 100;
    private const int MaximumLintFindings = 100;
    private static readonly string[] DefaultEnvironments = [DevelopmentEnvironment, ProductionEnvironment];
    private static readonly string[] DependencyCheckSeverities = ["CRITICAL", "HIGH", "MEDIUM", "LOW", "UNKNOWN"];
    private static readonly string[] MandatoryLintCategories =
    [
        "Dependency Restore",
        "Build / Analyzers",
        "Formatting",
        "Unit Tests",
        "Static Lint",
        "Type Safety",
        "Standards / Source Contracts"
    ];

    private readonly CodeQualityGitHubSecurityClient _gitHubClient;
    private readonly CodeQualitySonarClient _sonarClient;

    public CodeQualitySecurityService(HttpClient httpClient)
    {
        _sonarClient = new CodeQualitySonarClient(httpClient);
        _gitHubClient = new CodeQualityGitHubSecurityClient(httpClient);
    }

    public async Task<CodeQualitySecuritySnapshotDto> GetSnapshotAsync(
        CodeQualitySecurityOptions options,
        string? applicationKey,
        string? environment,
        CancellationToken cancellationToken,
        BuildDeploymentJenkinsOptions? jenkinsOptions = null)
    {
        var applications = options.Applications
            .Where(application => !string.IsNullOrWhiteSpace(application.Key))
            .ToArray();
        var selectedApplication = ResolveApplication(applications, applicationKey);
        var selectedEnvironment = ResolveEnvironment(environment);
        var applicationOptions = CreateApplicationOptions(applications);

        if (selectedApplication is null)
        {
            return CreateWarningSnapshot(
                applicationOptions,
                DefaultEnvironments,
                string.Empty,
                selectedEnvironment,
                options,
                "No Code Quality applications are configured.");
        }

        var sonarComponent = ResolveSonarProject(selectedApplication, selectedEnvironment);
        var gitHubRepository = ResolveGitHubRepository(selectedApplication, selectedEnvironment);
        var gitHubCodeScanningRef = ResolveGitHubCodeScanningRef(selectedApplication, selectedEnvironment);
        var gitHubOrganization = ResolveGitHubOrganization(options, selectedApplication);
        if (IsRepositoryMissing(selectedApplication, selectedEnvironment, gitHubRepository))
        {
            return CreateWarningSnapshot(
                applicationOptions,
                DefaultEnvironments,
                selectedApplication.Key,
                selectedEnvironment,
                options,
                UnavailableEnvironmentDetail);
        }

        var warnings = new List<string>();
        var providerStatuses = new List<CodeQualityProviderStatusDto>();
        var sonarData = await LoadSonarDataAsync(options, sonarComponent, warnings, providerStatuses, cancellationToken);
        sonarData = ApplyAiCodeAnalysisZeroFindings(options, selectedApplication, selectedEnvironment, sonarData, providerStatuses, jenkinsOptions);
        var gitHubData = await LoadGitHubDataAsync(options, gitHubOrganization, gitHubRepository, gitHubCodeScanningRef, warnings, providerStatuses, cancellationToken);
        sonarData = ApplySonarFreshnessGate(sonarData, gitHubData, selectedApplication, selectedEnvironment, jenkinsOptions, warnings, providerStatuses);
        var dependencyCheckData = LoadDependencyCheckData(options, selectedApplication, selectedEnvironment, warnings, providerStatuses, jenkinsOptions);
        var cycloneDxData = LoadCycloneDxBomData(options, selectedApplication, selectedEnvironment, warnings, providerStatuses, jenkinsOptions);
        var playwrightData = LoadPlaywrightData(options, selectedApplication, selectedEnvironment, warnings, providerStatuses, jenkinsOptions);
        var lintRequest = new LintReportLoadRequest(
            selectedApplication,
            selectedEnvironment,
            gitHubCodeScanningRef,
            gitHubData.CodeScanningHeadCommitSha,
            jenkinsOptions);
        var lintData = LoadLintReportData(options, lintRequest, warnings, providerStatuses);

        var hasSecurityWarning = sonarData.VulnerabilityTotalCount > 0
            || sonarData.BugTotalCount > 0
            || sonarData.CodeSmellTotalCount > 0
            || gitHubData.SeverityCounts.Any(count => count.Count > 0)
            || gitHubData.CodeScanningSeverityCounts.Any(count => count.Count > 0)
            || gitHubData.SecretScanningCounts.Any(count => count.Count > 0)
            || dependencyCheckData.VulnerabilityCount > 0
            || playwrightData.FailedTests > 0
            || lintData.ErrorCount > 0
            || lintData.ToolsFailed > 0;
        var hasProductionCriticalAlert = string.Equals(selectedEnvironment, ProductionEnvironment, StringComparison.OrdinalIgnoreCase)
            && HasCriticalProductionAlert(warnings, sonarData, gitHubData, dependencyCheckData, playwrightData);
        return new CodeQualitySecuritySnapshotDto
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Status = BuildSnapshotStatus(warnings, hasSecurityWarning, hasProductionCriticalAlert),
            StatusDetail = BuildStatusDetail(warnings, hasSecurityWarning, hasProductionCriticalAlert, selectedApplication, selectedEnvironment),
            SelectedApplicationKey = selectedApplication.Key,
            SelectedEnvironment = selectedEnvironment,
            SonarComponent = sonarComponent,
            SonarAnalysisDateUtc = sonarData.AnalysisDateUtc,
            SonarAnalysisRevision = sonarData.AnalysisRevision,
            SonarAnalysisVersion = sonarData.AnalysisVersion,
            GitHubRepository = gitHubRepository,
            GitHubCodeScanningRef = gitHubData.CodeScanningRef,
            GitHubCodeScanningHeadCommitSha = gitHubData.CodeScanningHeadCommitSha,
            GitHubCodeScanningAnalysisCommitSha = gitHubData.CodeScanningAnalysisCommitSha,
            GitHubCodeScanningAnalysisDateUtc = gitHubData.CodeScanningAnalysisDateUtc,
            GitHubCodeScanningAnalysisKey = gitHubData.CodeScanningAnalysisKey,
            GitHubCodeScanningAnalysisCategory = gitHubData.CodeScanningAnalysisCategory,
            GitHubCodeScanningIsCurrent = gitHubData.CodeScanningIsCurrent,
            GitHubCodeScanningIsAnalysisPending = gitHubData.CodeScanningIsAnalysisPending,
            GitHubCodeScanningAnalysisRunUrl = gitHubData.CodeScanningAnalysisRunUrl,
            GitHubCodeScanningFreshnessDetail = BuildCodeScanningFreshnessDetail(gitHubData),
            GitHubDashboardUrl = options.GitHubDashboardUrl,
            Applications = applicationOptions,
            Environments = DefaultEnvironments,
            SonarMetrics = sonarData.Metrics,
            CodeSmells = sonarData.CodeSmells,
            CodeSmellTotalCount = sonarData.CodeSmellTotalCount,
            CodeSmellDisplayedCount = sonarData.CodeSmells.Length,
            CodeSmellDetailsTruncated = sonarData.CodeSmellTotalCount > sonarData.CodeSmells.Length,
            Vulnerabilities = sonarData.Vulnerabilities,
            VulnerabilityTotalCount = sonarData.VulnerabilityTotalCount,
            VulnerabilityDisplayedCount = sonarData.Vulnerabilities.Length,
            VulnerabilityDetailsTruncated = sonarData.VulnerabilityTotalCount > sonarData.Vulnerabilities.Length,
            Bugs = sonarData.Bugs,
            BugTotalCount = sonarData.BugTotalCount,
            BugDisplayedCount = sonarData.Bugs.Length,
            BugDetailsTruncated = sonarData.BugTotalCount > sonarData.Bugs.Length,
            GitHubSeverityCounts = gitHubData.SeverityCounts,
            GitHubAlerts = gitHubData.Alerts,
            GitHubCodeScanningSeverityCounts = gitHubData.CodeScanningSeverityCounts,
            GitHubCodeScanningAlerts = gitHubData.CodeScanningAlerts,
            GitHubSecretScanningCounts = gitHubData.SecretScanningCounts,
            GitHubSecretScanningAlerts = gitHubData.SecretScanningAlerts,
            DependencyCheckStatus = dependencyCheckData.Status,
            DependencyCheckStatusDetail = dependencyCheckData.StatusDetail,
            DependencyCheckProjectName = dependencyCheckData.ProjectName,
            DependencyCheckEngineVersion = dependencyCheckData.EngineVersion,
            DependencyCheckReportPath = dependencyCheckData.ReportPath,
            DependencyCheckReportDateUtc = dependencyCheckData.ReportDateUtc,
            DependencyCheckDependenciesScanned = dependencyCheckData.DependenciesScanned,
            DependencyCheckVulnerabilityCount = dependencyCheckData.VulnerabilityCount,
            DependencyCheckSeverityCounts = dependencyCheckData.SeverityCounts,
            DependencyCheckFindings = dependencyCheckData.Findings,
            CycloneDxStatus = cycloneDxData.Status,
            CycloneDxStatusDetail = cycloneDxData.StatusDetail,
            CycloneDxBomPath = cycloneDxData.BomPath,
            CycloneDxBomFormat = cycloneDxData.BomFormat,
            CycloneDxSpecVersion = cycloneDxData.SpecVersion,
            CycloneDxSerialNumber = cycloneDxData.SerialNumber,
            CycloneDxGeneratedAtUtc = cycloneDxData.GeneratedAtUtc,
            CycloneDxComponentCount = cycloneDxData.ComponentCount,
            CycloneDxComponents = cycloneDxData.Components,
            LintStatus = lintData.Status,
            LintStatusDetail = lintData.StatusDetail,
            LintGeneratedAtUtc = lintData.GeneratedAtUtc,
            LintTotalFindings = lintData.TotalFindings,
            LintDisplayedCount = lintData.Findings.Length,
            LintErrorCount = lintData.ErrorCount,
            LintWarningCount = lintData.WarningCount,
            LintToolsTotal = lintData.ToolsTotal,
            LintToolsPassed = lintData.ToolsPassed,
            LintToolsFailed = lintData.ToolsFailed,
            LintToolsNotApplicable = lintData.ToolsNotApplicable,
            LintToolsNotConfigured = lintData.ToolsNotConfigured,
            LintToolResults = lintData.Tools,
            LintFindings = lintData.Findings,
            PlaywrightStatus = playwrightData.Status,
            PlaywrightStatusDetail = playwrightData.StatusDetail,
            PlaywrightProjectName = playwrightData.ProjectName,
            PlaywrightBaseUrl = playwrightData.BaseUrl,
            PlaywrightGeneratedAtUtc = playwrightData.GeneratedAtUtc,
            PlaywrightTotalTests = playwrightData.TotalTests,
            PlaywrightPassedTests = playwrightData.PassedTests,
            PlaywrightFailedTests = playwrightData.FailedTests,
            PlaywrightSkippedTests = playwrightData.SkippedTests,
            PlaywrightDurationSeconds = playwrightData.DurationSeconds,
            PlaywrightResults = playwrightData.Results,
            PlaywrightWorkflowContracts = playwrightData.WorkflowContracts,
            ProviderStatuses = providerStatuses
        };
    }

    private static CycloneDxBomData LoadCycloneDxBomData(
        CodeQualitySecurityOptions options,
        CodeQualityApplicationOptions application,
        string environment,
        List<string> warnings,
        List<CodeQualityProviderStatusDto> providerStatuses,
        BuildDeploymentJenkinsOptions? jenkinsOptions)
    {
        var bomPath = string.Empty;
        try
        {
            var resolvedBom = ResolveCycloneDxBomPath(options, application, environment, jenkinsOptions);
            bomPath = resolvedBom.Path;
            var trustedRoot = resolvedBom.TrustedRoot;
            if (string.IsNullOrWhiteSpace(bomPath))
            {
                AddProviderFailureStatus(providerStatuses, CycloneDxProviderName, ProviderUnavailableStatus, "CycloneDX SBOM path is not configured.");
                return CycloneDxBomData.Empty;
            }

            var fullBomPath = ResolveTrustedReportPath(bomPath, trustedRoot);
            if (!TrustedFileExists(fullBomPath))
            {
                var detail = $"CycloneDX SBOM was not found at {fullBomPath}.";
                if (!string.IsNullOrWhiteSpace(resolvedBom.Diagnostic))
                {
                    detail = $"{detail} {resolvedBom.Diagnostic}";
                }

                throw new FileNotFoundException(detail);
            }

            using var document = JsonDocument.Parse(ReadTrustedText(fullBomPath));
            var data = ParseCycloneDxBom(document.RootElement, fullBomPath);
            if (string.Equals(data.Status, WarningStatus, StringComparison.OrdinalIgnoreCase))
            {
                AddProviderFailureStatus(providerStatuses, CycloneDxProviderName, WarningStatus, data.StatusDetail);
                warnings.Add(data.StatusDetail);
            }
            else
            {
                AddProviderSuccess(providerStatuses, CycloneDxProviderName, $"CycloneDX SBOM loaded from {fullBomPath}.");
            }

            return data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var detail = $"Unable to load CycloneDX SBOM. {ex.Message}";
            AddProviderFailure(providerStatuses, warnings, CycloneDxProviderName, ProviderUnavailableStatus, detail);
            return CycloneDxBomData.Empty with
            {
                Status = ProviderUnavailableStatus,
                StatusDetail = detail,
                BomPath = bomPath
            };
        }
    }

    private static DependencyCheckReportData LoadDependencyCheckData(
        CodeQualitySecurityOptions options,
        CodeQualityApplicationOptions application,
        string environment,
        List<string> warnings,
        List<CodeQualityProviderStatusDto> providerStatuses,
        BuildDeploymentJenkinsOptions? jenkinsOptions)
    {
        var reportPath = string.Empty;
        try
        {
            var resolvedReport = ResolveDependencyCheckReportPath(options, application, environment, jenkinsOptions);
            reportPath = resolvedReport.Path;
            var trustedRoot = resolvedReport.TrustedRoot;
            if (string.IsNullOrWhiteSpace(reportPath))
            {
                AddProviderFailureStatus(providerStatuses, DependencyCheckProviderName, ProviderUnavailableStatus, "OWASP Dependency-Check report path is not configured.");
                return DependencyCheckReportData.Empty;
            }

            var fullReportPath = ResolveTrustedReportPath(reportPath, trustedRoot);
            if (!TrustedFileExists(fullReportPath))
            {
                var detail = $"OWASP Dependency-Check report was not found at {fullReportPath}.";
                if (!string.IsNullOrWhiteSpace(resolvedReport.Diagnostic))
                {
                    detail = $"{detail} {resolvedReport.Diagnostic}";
                }

                throw new FileNotFoundException(detail);
            }

            using var document = JsonDocument.Parse(ReadTrustedText(fullReportPath));
            var data = ParseDependencyCheckReport(document.RootElement, fullReportPath);
            data = ApplyDependencyCheckTrustChecks(data, application, environment, fullReportPath, options.JenkinsWorkspaceRoot);
            if (IsDependencyCheckTrustWarning(data))
            {
                AddProviderFailureStatus(providerStatuses, DependencyCheckProviderName, WarningStatus, data.StatusDetail);
                warnings.Add(data.StatusDetail);
                data = SuppressUntrustedDependencyCheckFindings(data);
            }
            else
            {
                AddProviderSuccess(providerStatuses, DependencyCheckProviderName, $"OWASP Dependency-Check report loaded from {fullReportPath}.");
            }

            return data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var detail = $"Unable to load OWASP Dependency-Check report. {ex.Message}";
            AddProviderFailure(providerStatuses, warnings, DependencyCheckProviderName, ProviderUnavailableStatus, detail);
            return DependencyCheckReportData.Empty with
            {
                Status = ProviderUnavailableStatus,
                StatusDetail = detail,
                ReportPath = reportPath
            };
        }
    }

    private static PlaywrightReportData LoadPlaywrightData(
        CodeQualitySecurityOptions options,
        CodeQualityApplicationOptions application,
        string environment,
        List<string> warnings,
        List<CodeQualityProviderStatusDto> providerStatuses,
        BuildDeploymentJenkinsOptions? jenkinsOptions)
    {
        try
        {
            var resolvedReport = ResolvePlaywrightReportPath(options, application, environment, jenkinsOptions);
            var reportPath = resolvedReport.Path;
            if (string.IsNullOrWhiteSpace(reportPath))
            {
                AddProviderFailureStatus(providerStatuses, PlaywrightProviderName, ProviderUnavailableStatus, "Playwright report path is not configured.");
                return PlaywrightReportData.Empty;
            }

            var fullReportPath = ResolveTrustedReportPath(reportPath, resolvedReport.TrustedRoot);
            if (!TrustedFileExists(fullReportPath))
            {
                var detail = $"Playwright unavailable. Expected: {fullReportPath}. File not found.";
                if (!string.IsNullOrWhiteSpace(resolvedReport.Diagnostic))
                {
                    detail = $"{detail} {resolvedReport.Diagnostic}";
                }

                throw new FileNotFoundException(detail);
            }

            using var document = JsonDocument.Parse(ReadTrustedText(fullReportPath));
            var data = ParsePlaywrightReport(document.RootElement, application);
            data = ApplyPlaywrightTrustChecks(data, application);
            if (IsPlaywrightTrustWarning(data))
            {
                AddProviderFailureStatus(providerStatuses, PlaywrightProviderName, WarningStatus, data.StatusDetail);
                warnings.Add(data.StatusDetail);
                return SuppressUntrustedPlaywrightResults(data);
            }

            AddProviderSuccess(providerStatuses, PlaywrightProviderName, $"Playwright report loaded from {fullReportPath}.");
            if (data.FailedTests > 0)
            {
                warnings.Add(data.StatusDetail);
            }

            return data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var detail = $"Unable to load Playwright report. {ex.Message}";
            AddProviderFailure(providerStatuses, warnings, PlaywrightProviderName, ProviderUnavailableStatus, detail);
            return PlaywrightReportData.Empty with
            {
                Status = ProviderUnavailableStatus,
                StatusDetail = detail
            };
        }
    }


    private static LintReportData LoadLintReportData(
        CodeQualitySecurityOptions options,
        LintReportLoadRequest request,
        List<string> warnings,
        List<CodeQualityProviderStatusDto> providerStatuses)
    {
        var reportPath = string.Empty;
        try
        {
            var resolvedReport = ResolveLintReportPath(options, request.Application, request.Environment, request.JenkinsOptions);
            reportPath = resolvedReport.Path;
            if (string.IsNullOrWhiteSpace(reportPath))
            {
                AddProviderFailureStatus(providerStatuses, LintProviderName, ProviderUnavailableStatus, "Lint & Standards report path is not configured.");
                return LintReportData.Empty;
            }

            var fullReportPath = ResolveTrustedReportPath(reportPath, resolvedReport.TrustedRoot);
            if (!TrustedFileExists(fullReportPath))
            {
                var detail = $"Lint & Standards report was not found at {fullReportPath}.";
                if (!string.IsNullOrWhiteSpace(resolvedReport.Diagnostic))
                {
                    detail = $"{detail} {resolvedReport.Diagnostic}";
                }

                throw new FileNotFoundException(detail);
            }

            using var document = JsonDocument.Parse(ReadTrustedText(fullReportPath));
            var data = ParseLintReport(document.RootElement);
            data = ApplyLintTrustChecks(data, request.Application, request.Environment, request.ExpectedBranch, request.ExpectedCommit);
            if (IsLintWarning(data))
            {
                AddProviderFailureStatus(providerStatuses, LintProviderName, WarningStatus, data.StatusDetail);
                warnings.Add(data.StatusDetail);
                if (!data.IsTrusted)
                {
                    data = SuppressUntrustedLintFindings(data);
                }
            }
            else
            {
                AddProviderSuccess(providerStatuses, LintProviderName, $"Lint & Standards report loaded from {fullReportPath}.");
            }

            return data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var detail = $"Unable to load Lint & Standards report. {ex.Message}";
            AddProviderFailure(providerStatuses, warnings, LintProviderName, ProviderUnavailableStatus, detail);
            return LintReportData.Empty with
            {
                Status = ProviderUnavailableStatus,
                StatusDetail = detail
            };
        }
    }
    private async Task<SonarSnapshotData> LoadSonarDataAsync(
        CodeQualitySecurityOptions options,
        string sonarComponent,
        List<string> warnings,
        List<CodeQualityProviderStatusDto> providerStatuses,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.SonarBaseUrl) || string.IsNullOrWhiteSpace(sonarComponent))
        {
            AddProviderFailure(providerStatuses, warnings, SonarProviderName, ProviderUnavailableStatus, "SonarQube settings are missing.");
            return SonarSnapshotData.Empty;
        }

        try
        {
            var sonarBaseUrl = options.SonarBaseUrl.TrimEnd('/');
            var metrics = await _sonarClient.GetMetricsAsync(sonarBaseUrl, sonarComponent, cancellationToken);
            var vulnerabilities = await _sonarClient.GetIssuesAsync(sonarBaseUrl, sonarComponent, "VULNERABILITY", cancellationToken);
            var bugs = await _sonarClient.GetIssuesAsync(sonarBaseUrl, sonarComponent, "BUG", cancellationToken);
            var codeSmells = await _sonarClient.GetIssuesAsync(sonarBaseUrl, sonarComponent, "CODE_SMELL", cancellationToken);
            var latestAnalysis = await LoadSonarLatestAnalysisAsync(sonarBaseUrl, sonarComponent, cancellationToken);
            metrics = ReconcileSonarIssueMetrics(
                metrics,
                sonarComponent,
                vulnerabilities.TotalCount,
                bugs.TotalCount,
                codeSmells.TotalCount,
                warnings);
            AddProviderSuccess(providerStatuses, SonarProviderName, BuildSonarSuccessDetail(latestAnalysis));
            return new SonarSnapshotData(metrics, vulnerabilities.Issues, bugs.Issues, codeSmells.Issues, vulnerabilities.TotalCount, bugs.TotalCount, codeSmells.TotalCount, latestAnalysis?.DateUtc, latestAnalysis?.Revision ?? string.Empty, latestAnalysis?.Version ?? string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddProviderFailure(providerStatuses, warnings, SonarProviderName, ClassifyProviderFailure(ex), $"Unable to load SonarQube data. {ex.Message}");
            return SonarSnapshotData.Empty;
        }
    }

    private static SonarSnapshotData ApplyAiCodeAnalysisZeroFindings(
        CodeQualitySecurityOptions options,
        CodeQualityApplicationOptions application,
        string environment,
        SonarSnapshotData sonarData,
        List<CodeQualityProviderStatusDto> providerStatuses,
        BuildDeploymentJenkinsOptions? jenkinsOptions)
    {
        if (HasProviderSuccess(providerStatuses, SonarProviderName))
        {
            return sonarData;
        }

        var aiCodeAnalysisPath = ResolveSystemHealthReportPath(options, application, environment, jenkinsOptions, manifest => manifest.CodeAnalysisPath, "aiCodeAnalysis");
        if (string.IsNullOrWhiteSpace(aiCodeAnalysisPath.Path)
            || !AiCodeAnalysisHasZeroFindings(aiCodeAnalysisPath.Path, aiCodeAnalysisPath.TrustedRoot))
        {
            return sonarData;
        }

        return sonarData with
        {
            Metrics =
            [
                CreateZeroSonarMetric("vulnerabilities", "Vulnerabilities"),
                CreateZeroSonarMetric("bugs", "Bugs"),
                CreateZeroSonarMetric("code_smells", "Code Smells")
            ],
            Vulnerabilities = Array.Empty<SonarIssueDto>(),
            Bugs = Array.Empty<SonarIssueDto>(),
            CodeSmells = Array.Empty<SonarIssueDto>(),
            VulnerabilityTotalCount = 0,
            BugTotalCount = 0,
            CodeSmellTotalCount = 0
        };
    }

    private async Task<SonarLatestAnalysisDto?> LoadSonarLatestAnalysisAsync(
        string sonarBaseUrl,
        string sonarComponent,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _sonarClient.GetLatestAnalysisAsync(sonarBaseUrl, sonarComponent, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string BuildSonarSuccessDetail(SonarLatestAnalysisDto? latestAnalysis)
    {
        if (latestAnalysis is null)
        {
            return "SonarQube data loaded. Latest analysis metadata was not available.";
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(latestAnalysis.Version))
        {
            parts.Add($"analysis {latestAnalysis.Version}");
        }

        if (latestAnalysis.DateUtc is { } dateUtc)
        {
            parts.Add($"run {dateUtc:yyyy-MM-dd HH:mm:ss}Z");
        }

        if (!string.IsNullOrWhiteSpace(latestAnalysis.Revision))
        {
            parts.Add($"revision {ShortRevision(latestAnalysis.Revision)}");
        }

        return parts.Count == 0
            ? "SonarQube data loaded. Latest analysis metadata was not available."
            : $"SonarQube data loaded from {string.Join(", ", parts)}.";
    }

    private static string ShortRevision(string revision)
    {
        return revision.Length > 8 ? revision[..8] : revision;
    }

    private static SonarSnapshotData ApplySonarFreshnessGate(
        SonarSnapshotData sonarData,
        GitHubSecurityData gitHubData,
        CodeQualityApplicationOptions application,
        string environment,
        BuildDeploymentJenkinsOptions? jenkinsOptions,
        List<string> warnings,
        List<CodeQualityProviderStatusDto> providerStatuses)
    {
        if (string.IsNullOrWhiteSpace(sonarData.AnalysisRevision)
            || string.IsNullOrWhiteSpace(gitHubData.CodeScanningHeadCommitSha)
            || IsSameCommit(sonarData.AnalysisRevision, gitHubData.CodeScanningHeadCommitSha))
        {
            return sonarData;
        }

        var detail = BuildStaleSonarDetail(sonarData.AnalysisRevision, gitHubData.CodeScanningHeadCommitSha, application, environment, jenkinsOptions);
        providerStatuses.RemoveAll(status => string.Equals(status.Provider, SonarProviderName, StringComparison.OrdinalIgnoreCase));
        AddProviderFailure(providerStatuses, warnings, SonarProviderName, WarningStatus, detail);
        return sonarData;
    }

    private static string BuildStaleSonarDetail(
        string sonarRevision,
        string branchHeadRevision,
        CodeQualityApplicationOptions application,
        string environment,
        BuildDeploymentJenkinsOptions? jenkinsOptions)
    {
        var jobName = ResolveJenkinsJobName(application, environment, jenkinsOptions);
        var buildInstruction = string.IsNullOrWhiteSpace(jobName)
            ? "Run the configured Jenkins build, wait for it to finish, then refresh this page."
            : $"Run the {jobName} Jenkins build, wait for it to finish, then refresh this page.";

        return $"SonarQube data is stale. Branch HEAD {ShortRevision(branchHeadRevision)} has not been analysed by SonarQube. Latest SonarQube analysis is {ShortRevision(sonarRevision)}. Displaying latest available SonarQube metrics as stale evidence until the latest commit is analysed. {buildInstruction}";
    }

    private static bool IsSameCommit(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return left.StartsWith(right, StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GitHubSecurityData> LoadGitHubDataAsync(
        CodeQualitySecurityOptions options,
        string gitHubOrganization,
        string gitHubRepository,
        string gitHubCodeScanningRef,
        List<string> warnings,
        List<CodeQualityProviderStatusDto> providerStatuses,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.GitHubToken))
        {
            AddGitHubProviderFailures(providerStatuses, warnings, ProviderUnavailableStatus, "GitHub security token is not configured.");
            return GitHubSecurityData.Empty;
        }

        if (string.IsNullOrWhiteSpace(gitHubOrganization) || string.IsNullOrWhiteSpace(gitHubRepository))
        {
            AddGitHubProviderFailures(providerStatuses, warnings, ProviderUnavailableStatus, "GitHub security repository settings are missing.");
            return GitHubSecurityData.Empty;
        }

        try
        {
            var dependencyData = await LoadGitHubDependencyDataAsync(options, gitHubOrganization, gitHubRepository, warnings, providerStatuses, cancellationToken);
            var codeScanningData = await LoadGitHubCodeScanningDataAsync(options, gitHubOrganization, gitHubRepository, gitHubCodeScanningRef, warnings, providerStatuses, cancellationToken);
            var secretScanningData = await LoadGitHubSecretScanningDataAsync(options, gitHubOrganization, gitHubRepository, warnings, providerStatuses, cancellationToken);
            return new GitHubSecurityData(
                dependencyData.SeverityCounts,
                dependencyData.Alerts,
                codeScanningData.Ref,
                codeScanningData.HeadCommitSha,
                codeScanningData.AnalysisCommitSha,
                codeScanningData.AnalysisDateUtc,
                codeScanningData.AnalysisKey,
                codeScanningData.AnalysisCategory,
                codeScanningData.IsCurrent,
                codeScanningData.IsAnalysisPending,
                codeScanningData.AnalysisRunUrl,
                codeScanningData.SeverityCounts,
                codeScanningData.Alerts,
                secretScanningData.Counts,
                secretScanningData.Alerts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddGitHubProviderFailures(providerStatuses, warnings, ClassifyProviderFailure(ex), $"Unable to load GitHub security data. {ex.Message}");
            return GitHubSecurityData.Empty;
        }
    }

    private async Task<GitHubSecurityData> LoadGitHubDependencyDataAsync(
        CodeQualitySecurityOptions options,
        string gitHubOrganization,
        string gitHubRepository,
        List<string> warnings,
        List<CodeQualityProviderStatusDto> providerStatuses,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await _gitHubClient.GetSecurityAsync(options, gitHubOrganization, gitHubRepository, cancellationToken);
            AddProviderSuccess(providerStatuses, "GitHub Dependabot", "GitHub Dependabot data loaded.");
            return data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddProviderFailure(providerStatuses, warnings, "GitHub Dependabot", ClassifyProviderFailure(ex), $"Unable to load GitHub Dependabot data. {ex.Message}");
            return GitHubSecurityData.Empty;
        }
    }

    private async Task<GitHubCodeScanningData> LoadGitHubCodeScanningDataAsync(
        CodeQualitySecurityOptions options,
        string gitHubOrganization,
        string gitHubRepository,
        string gitHubCodeScanningRef,
        List<string> warnings,
        List<CodeQualityProviderStatusDto> providerStatuses,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await _gitHubClient.GetCodeScanningAsync(options, gitHubOrganization, gitHubRepository, gitHubCodeScanningRef, cancellationToken);
            var freshnessDetail = BuildCodeScanningFreshnessDetail(data);
            if (IsCodeScanningStale(data))
            {
                AddProviderFailure(providerStatuses, warnings, GitHubCodeQlProviderName, WarningStatus, freshnessDetail);
                return SuppressStaleCodeScanningFindings(data);
            }
            else
            {
                AddProviderSuccess(providerStatuses, GitHubCodeQlProviderName, freshnessDetail);
            }

            return data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddProviderFailure(providerStatuses, warnings, GitHubCodeQlProviderName, ClassifyProviderFailure(ex), $"Unable to load {GitHubCodeQlProviderName} data. {ex.Message}");
            return GitHubCodeScanningData.Empty;
        }
    }

    private static bool IsCodeScanningStale(GitHubCodeScanningData data)
    {
        return !string.IsNullOrWhiteSpace(data.HeadCommitSha)
            && !data.IsCurrent;
    }

    private static string BuildCodeScanningFreshnessDetail(GitHubCodeScanningData data)
    {
        if (string.IsNullOrWhiteSpace(data.Ref))
        {
            return $"{GitHubCodeQlProviderName} data loaded.";
        }

        if (string.IsNullOrWhiteSpace(data.HeadCommitSha))
        {
            return $"{GitHubCodeQlProviderName} data loaded for {data.Ref}. Branch freshness could not be determined.";
        }

        var headCommit = ShortRevision(data.HeadCommitSha);
        if (data.IsAnalysisPending)
        {
            var pendingRun = string.IsNullOrWhiteSpace(data.AnalysisRunUrl)
                ? string.Empty
                : $" Run: {data.AnalysisRunUrl}.";
            return $"{GitHubCodeQlProviderName} analysis is pending for {data.Ref}. Branch HEAD {headCommit} is waiting for the CodeQL workflow to finish before alerts are trusted.{pendingRun}";
        }

        if (string.IsNullOrWhiteSpace(data.AnalysisCommitSha))
        {
            return $"{GitHubCodeQlProviderName} data is stale for {data.Ref}. Branch HEAD {headCommit} has not been analysed by CodeQL.";
        }

        var analysisCommit = ShortRevision(data.AnalysisCommitSha);
        var analysisDate = data.AnalysisDateUtc.HasValue
            ? $" from {data.AnalysisDateUtc.Value:O}"
            : string.Empty;

        return data.IsCurrent
            ? $"{GitHubCodeQlProviderName} data loaded for {data.Ref}. Latest CodeQL analysis matches branch HEAD {headCommit}{analysisDate}."
            : $"{GitHubCodeQlProviderName} data is stale for {data.Ref}. Branch HEAD {headCommit} has not been analysed. Latest CodeQL analysis is {analysisCommit}{analysisDate}.";
    }

    private static string BuildCodeScanningFreshnessDetail(GitHubSecurityData data)
    {
        return BuildCodeScanningFreshnessDetail(new GitHubCodeScanningData(
            data.CodeScanningRef,
            data.CodeScanningHeadCommitSha,
            data.CodeScanningAnalysisCommitSha,
            data.CodeScanningAnalysisDateUtc,
            data.CodeScanningAnalysisKey,
            data.CodeScanningAnalysisCategory,
            data.CodeScanningIsCurrent,
            data.CodeScanningIsAnalysisPending,
            data.CodeScanningAnalysisRunUrl,
            data.CodeScanningSeverityCounts,
            data.CodeScanningAlerts));
    }

    private static GitHubCodeScanningData SuppressStaleCodeScanningFindings(GitHubCodeScanningData data)
    {
        return data with
        {
            SeverityCounts = GitHubCodeScanningData.Empty.SeverityCounts,
            Alerts = Array.Empty<GitHubCodeScanningAlertDto>()
        };
    }

    private async Task<GitHubSecretScanningData> LoadGitHubSecretScanningDataAsync(
        CodeQualitySecurityOptions options,
        string gitHubOrganization,
        string gitHubRepository,
        List<string> warnings,
        List<CodeQualityProviderStatusDto> providerStatuses,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await _gitHubClient.GetSecretScanningAsync(options, gitHubOrganization, gitHubRepository, cancellationToken);
            AddProviderSuccess(providerStatuses, "GitHub Secret Scanning", "GitHub secret scanning data loaded.");
            return data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddProviderFailure(providerStatuses, warnings, "GitHub Secret Scanning", ClassifyProviderFailure(ex), $"Unable to load GitHub secret scanning data. {ex.Message}");
            return GitHubSecretScanningData.Empty;
        }
    }

    private static void AddProviderSuccess(
        List<CodeQualityProviderStatusDto> providerStatuses,
        string provider,
        string detail)
    {
        providerStatuses.Add(new CodeQualityProviderStatusDto
        {
            Provider = provider,
            Status = ProviderSuccessStatus,
            Detail = detail,
            CheckedAtUtc = DateTime.UtcNow
        });
    }

    private static void AddProviderFailure(
        List<CodeQualityProviderStatusDto> providerStatuses,
        List<string> warnings,
        string provider,
        string status,
        string detail)
    {
        AddProviderFailureStatus(providerStatuses, provider, status, detail);
        warnings.Add(detail);
    }

    private static void AddGitHubProviderFailures(
        List<CodeQualityProviderStatusDto> providerStatuses,
        List<string> warnings,
        string status,
        string detail)
    {
        AddProviderFailureStatus(providerStatuses, "GitHub Dependabot", status, detail);
        AddProviderFailureStatus(providerStatuses, GitHubCodeQlProviderName, status, detail);
        AddProviderFailureStatus(providerStatuses, "GitHub Secret Scanning", status, detail);
        warnings.Add(detail);
    }

    private static void AddProviderFailureStatus(
        List<CodeQualityProviderStatusDto> providerStatuses,
        string provider,
        string status,
        string detail)
    {
        providerStatuses.Add(new CodeQualityProviderStatusDto
        {
            Provider = provider,
            Status = status,
            Detail = detail,
            CheckedAtUtc = DateTime.UtcNow
        });
    }

    private static string ClassifyProviderFailure(Exception exception)
    {
        var message = exception.Message;
        return message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase)
            ? ProviderRateLimitedStatus
            : ProviderUnavailableStatus;
    }

    private static CodeQualityApplicationOptions? ResolveApplication(
        CodeQualityApplicationOptions[] applications,
        string? applicationKey)
    {
        if (!string.IsNullOrWhiteSpace(applicationKey))
        {
            var matchingApplication = applications.FirstOrDefault(application => string.Equals(application.Key, applicationKey, StringComparison.OrdinalIgnoreCase));
            if (matchingApplication is not null)
            {
                return matchingApplication;
            }
        }

        return applications.FirstOrDefault(application => string.Equals(application.Key, DefaultApplicationKey, StringComparison.OrdinalIgnoreCase))
            ?? (applications.Length > 0 ? applications[0] : null);
    }

    private static CodeQualityApplicationOptionDto[] CreateApplicationOptions(IEnumerable<CodeQualityApplicationOptions> applications)
    {
        return applications
            .Select(application => new CodeQualityApplicationOptionDto
            {
                Key = application.Key,
                Label = string.IsNullOrWhiteSpace(application.Label) ? application.Key : application.Label
            })
            .ToArray();
    }

    private static string ResolveEnvironment(string? environment)
    {
        if (string.IsNullOrWhiteSpace(environment))
        {
            return DevelopmentEnvironment;
        }

        var value = environment.Trim();
        if (value.Equals(DevelopmentEnvironment, StringComparison.OrdinalIgnoreCase)
            || value.Equals("dev", StringComparison.OrdinalIgnoreCase))
        {
            return DevelopmentEnvironment;
        }

        if (value.Equals(ProductionEnvironment, StringComparison.OrdinalIgnoreCase)
            || value.Equals("prod", StringComparison.OrdinalIgnoreCase))
        {
            return ProductionEnvironment;
        }

        return DevelopmentEnvironment;
    }

    private static string ResolveSonarProject(CodeQualityApplicationOptions application, string environment)
    {
        if (string.Equals(environment, DevelopmentEnvironment, StringComparison.OrdinalIgnoreCase))
        {
            return FirstConfigured(application.DevSonarProject, application.SonarProject);
        }

        return FirstConfigured(application.ProdSonarProject, application.SonarProject);
    }

    private static string ResolveGitHubRepository(CodeQualityApplicationOptions application, string environment)
    {
        return string.Equals(environment, DevelopmentEnvironment, StringComparison.Ordinal)
            ? application.DevGitHubRepository
            : application.ProdGitHubRepository;
    }

    private static string ResolveGitHubCodeScanningRef(CodeQualityApplicationOptions application, string environment)
    {
        return string.Equals(environment, DevelopmentEnvironment, StringComparison.Ordinal)
            ? FirstConfigured(application.DevGitHubRef, application.GitHubRef)
            : FirstConfigured(application.ProdGitHubRef, application.GitHubRef);
    }

    private static ResolvedReportPath ResolveDependencyCheckReportPath(
        CodeQualitySecurityOptions options,
        CodeQualityApplicationOptions application,
        string environment,
        BuildDeploymentJenkinsOptions? jenkinsOptions)
    {
        var configuredPath = string.Equals(environment, DevelopmentEnvironment, StringComparison.Ordinal)
            ? application.DevDependencyCheckReportPath
            : application.ProdDependencyCheckReportPath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return new ResolvedReportPath(ResolveTrustedReportPath(configuredPath, options.JenkinsWorkspaceRoot), options.JenkinsWorkspaceRoot);
        }

        var jenkinsPath = ResolveJenkinsDependencyCheckReportPath(options, application, environment, jenkinsOptions);
        if (!string.IsNullOrWhiteSpace(jenkinsPath.Path) && TrustedFileExists(jenkinsPath.Path))
        {
            return jenkinsPath;
        }

        var manifestPath = ResolveSystemHealthReportPath(options, application, environment, jenkinsOptions, manifest => manifest.DependencyCheckPath, "dependencyCheckReport", "dependencyCheck");
        if (!string.IsNullOrWhiteSpace(manifestPath.Path))
        {
            return manifestPath;
        }

        return jenkinsPath;
    }

    private static ResolvedReportPath ResolveCycloneDxBomPath(
        CodeQualitySecurityOptions options,
        CodeQualityApplicationOptions application,
        string environment,
        BuildDeploymentJenkinsOptions? jenkinsOptions)
    {
        var manifestPath = ResolveSystemHealthReportPath(options, application, environment, jenkinsOptions, manifest => manifest.CycloneDxBomPath, "cycloneDxBom", "sbom", "cycloneDx");
        if (!string.IsNullOrWhiteSpace(manifestPath.Path))
        {
            return manifestPath;
        }

        var configuredPath = string.Equals(environment, DevelopmentEnvironment, StringComparison.Ordinal)
            ? application.DevCycloneDxBomPath
            : application.ProdCycloneDxBomPath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return new ResolvedReportPath(ResolveTrustedReportPath(configuredPath, options.JenkinsWorkspaceRoot), options.JenkinsWorkspaceRoot);
        }

        return ResolveJenkinsCycloneDxBomPath(options, application, environment, jenkinsOptions);
    }

    private static ResolvedReportPath ResolveLintReportPath(
        CodeQualitySecurityOptions options,
        CodeQualityApplicationOptions application,
        string environment,
        BuildDeploymentJenkinsOptions? jenkinsOptions)
    {
        var manifestPath = ResolveSystemHealthReportPath(options, application, environment, jenkinsOptions, manifest => manifest.LintReportPath, "lintReport", "lint");
        if (!string.IsNullOrWhiteSpace(manifestPath.Path))
        {
            return manifestPath;
        }

        var configuredPath = string.Equals(environment, DevelopmentEnvironment, StringComparison.Ordinal)
            ? application.DevLintReportPath
            : application.ProdLintReportPath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return new ResolvedReportPath(ResolveTrustedReportPath(configuredPath, options.JenkinsWorkspaceRoot), options.JenkinsWorkspaceRoot);
        }

        return ResolveJenkinsLintReportPath(options, application, environment, jenkinsOptions);
    }

    private static ResolvedReportPath ResolveJenkinsLintReportPath(
        CodeQualitySecurityOptions options,
        CodeQualityApplicationOptions application,
        string environment,
        BuildDeploymentJenkinsOptions? jenkinsOptions)
    {
        var jobName = ResolveJenkinsJobName(application, environment, jenkinsOptions);
        if (string.IsNullOrWhiteSpace(jobName))
        {
            return ResolvedReportPath.Empty;
        }

        var safeJobName = ResolveSafeJenkinsJobName(jobName);
        if (string.IsNullOrWhiteSpace(safeJobName))
        {
            return ResolvedReportPath.Empty;
        }

        var workspaceCandidates = BuildLintReportCandidates(Path.Combine(options.JenkinsWorkspaceRoot, safeJobName));
        foreach (var candidate in workspaceCandidates)
        {
            var trustedCandidate = ResolveTrustedReportPath(candidate, options.JenkinsWorkspaceRoot);
            if (TrustedFileExists(trustedCandidate))
            {
                return new ResolvedReportPath(trustedCandidate, options.JenkinsWorkspaceRoot);
            }
        }

        var jenkinsHome = ResolveJenkinsHome(options.JenkinsHomeRoot, options.JenkinsWorkspaceRoot);
        var archiveDiagnostic = string.Empty;
        if (!string.IsNullOrWhiteSpace(jenkinsHome))
        {
            var archivePath = ResolveLatestArchivedReportPath(jenkinsHome, safeJobName, BuildLintReportCandidates, out archiveDiagnostic);
            if (!string.IsNullOrWhiteSpace(archivePath))
            {
                return new ResolvedReportPath(archivePath, jenkinsHome);
            }
        }

        return new ResolvedReportPath(workspaceCandidates[0], options.JenkinsWorkspaceRoot, archiveDiagnostic);
    }
    private static ResolvedReportPath ResolveJenkinsDependencyCheckReportPath(
        CodeQualitySecurityOptions options,
        CodeQualityApplicationOptions application,
        string environment,
        BuildDeploymentJenkinsOptions? jenkinsOptions)
    {
        var jobName = ResolveJenkinsJobName(application, environment, jenkinsOptions);
        if (string.IsNullOrWhiteSpace(jobName))
        {
            return ResolvedReportPath.Empty;
        }

        var safeJobName = ResolveSafeJenkinsJobName(jobName);
        if (string.IsNullOrWhiteSpace(safeJobName))
        {
            return ResolvedReportPath.Empty;
        }

        var workspaceCandidates = BuildDependencyCheckReportCandidates(
            Path.Combine(options.JenkinsWorkspaceRoot, safeJobName));
        foreach (var candidate in workspaceCandidates)
        {
            var trustedCandidate = ResolveTrustedReportPath(candidate, options.JenkinsWorkspaceRoot);
            if (TrustedFileExists(trustedCandidate))
            {
                return new ResolvedReportPath(trustedCandidate, options.JenkinsWorkspaceRoot);
            }
        }

        var jenkinsHome = ResolveJenkinsHome(options.JenkinsHomeRoot, options.JenkinsWorkspaceRoot);
        var archiveDiagnostic = string.Empty;
        if (!string.IsNullOrWhiteSpace(jenkinsHome))
        {
            var archivePath = ResolveLatestArchivedReportPath(jenkinsHome, safeJobName, BuildDependencyCheckReportCandidates, out archiveDiagnostic);
            if (!string.IsNullOrWhiteSpace(archivePath))
            {
                return new ResolvedReportPath(archivePath, jenkinsHome);
            }
        }

        return new ResolvedReportPath(workspaceCandidates[0], options.JenkinsWorkspaceRoot, archiveDiagnostic);
    }

    private static ResolvedReportPath ResolveJenkinsCycloneDxBomPath(
        CodeQualitySecurityOptions options,
        CodeQualityApplicationOptions application,
        string environment,
        BuildDeploymentJenkinsOptions? jenkinsOptions)
    {
        var jobName = ResolveJenkinsJobName(application, environment, jenkinsOptions);
        if (string.IsNullOrWhiteSpace(jobName))
        {
            return ResolvedReportPath.Empty;
        }

        var safeJobName = ResolveSafeJenkinsJobName(jobName);
        if (string.IsNullOrWhiteSpace(safeJobName))
        {
            return ResolvedReportPath.Empty;
        }

        var workspaceCandidates = BuildCycloneDxBomCandidates(
            Path.Combine(options.JenkinsWorkspaceRoot, safeJobName));
        foreach (var candidate in workspaceCandidates)
        {
            var trustedCandidate = ResolveTrustedReportPath(candidate, options.JenkinsWorkspaceRoot);
            if (TrustedFileExists(trustedCandidate))
            {
                return new ResolvedReportPath(trustedCandidate, options.JenkinsWorkspaceRoot);
            }
        }

        var jenkinsHome = ResolveJenkinsHome(options.JenkinsHomeRoot, options.JenkinsWorkspaceRoot);
        var archiveDiagnostic = string.Empty;
        if (!string.IsNullOrWhiteSpace(jenkinsHome))
        {
            var archivePath = ResolveLatestArchivedReportPath(jenkinsHome, safeJobName, BuildCycloneDxBomCandidates, out archiveDiagnostic);
            if (!string.IsNullOrWhiteSpace(archivePath))
            {
                return new ResolvedReportPath(archivePath, jenkinsHome);
            }
        }

        return new ResolvedReportPath(workspaceCandidates[0], options.JenkinsWorkspaceRoot, archiveDiagnostic);
    }

    private static string ResolveJenkinsJobName(
        CodeQualityApplicationOptions application,
        string environment,
        BuildDeploymentJenkinsOptions? jenkinsOptions)
    {
        var job = jenkinsOptions?.Jobs.FirstOrDefault(job =>
            string.Equals(job.Key, application.Key, StringComparison.OrdinalIgnoreCase));
        if (job is null)
        {
            return string.Empty;
        }

        return string.Equals(environment, DevelopmentEnvironment, StringComparison.OrdinalIgnoreCase)
            ? job.DevJobName
            : job.ProdJobName;
    }

    private static string ResolveSafeJenkinsJobName(string jobName)
    {
        var value = jobName.Trim();
        if (string.IsNullOrWhiteSpace(value)
            || !IsSafeJenkinsJobName(value)
            || value.Contains("..", StringComparison.Ordinal)
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || value.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathFullyQualified(value))
        {
            return string.Empty;
        }

        return value;
    }

    private static bool IsSafeJenkinsJobName(string jobName)
    {
        if (jobName.Length > 128)
        {
            return false;
        }

        foreach (var character in jobName)
        {
            if (!char.IsLetterOrDigit(character)
                && character != '-'
                && character != '_'
                && character != '.')
            {
                return false;
            }
        }

        return true;
    }

    private static string[] BuildDependencyCheckReportCandidates(string jobRoot)
    {
        return
        [
            Path.Combine(jobRoot, JenkinsArtifactRootDirectory, "dependency-check", "dependency-check-report.json"),
            Path.Combine(jobRoot, "dependency-check-report.json")
        ];
    }

    private static string[] BuildCycloneDxBomCandidates(string jobRoot)
    {
        return
        [
            Path.Combine(jobRoot, JenkinsArtifactRootDirectory, "sbom", "bom.json"),
            Path.Combine(jobRoot, "bom.json")
        ];
    }

    private static string[] BuildLintReportCandidates(string jobRoot)
    {
        return
        [
            Path.Combine(jobRoot, JenkinsArtifactRootDirectory, "lint", "lint-report.json")
        ];
    }

    private static string[] BuildPlaywrightReportCandidates(string jobRoot)
    {
        return
        [
            Path.Combine(jobRoot, "playwright-results.json"),
            Path.Combine(jobRoot, JenkinsArtifactRootDirectory, "playwright", "playwright-results.json")
        ];
    }

    private static string ResolveJenkinsHome(string jenkinsHomeRoot, string workspaceRoot)
    {
        var configuredJenkinsHome = ResolveTrustedRoot(jenkinsHomeRoot);
        if (!string.IsNullOrWhiteSpace(configuredJenkinsHome))
        {
            return configuredJenkinsHome;
        }

        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return string.Empty;
        }

        var expandedWorkspaceRoot = Environment.ExpandEnvironmentVariables(workspaceRoot);
        if (!Path.IsPathFullyQualified(expandedWorkspaceRoot))
        {
            return string.Empty;
        }

        var trimmedWorkspaceRoot = Path.GetFullPath(expandedWorkspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(trimmedWorkspaceRoot), "workspace", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var parentPath = Path.GetDirectoryName(trimmedWorkspaceRoot);
        return string.IsNullOrWhiteSpace(parentPath)
            ? string.Empty
            : Path.GetFullPath(parentPath);
    }

    private static string ResolveTrustedRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return string.Empty;
        }

        var expandedRoot = Environment.ExpandEnvironmentVariables(rootPath);
        if (!Path.IsPathFullyQualified(expandedRoot))
        {
            return string.Empty;
        }

        return Path.GetFullPath(expandedRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ResolveLatestArchivedReportPath(
        string jenkinsHome,
        string jobName,
        Func<string, string[]> buildReportCandidates,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        try
        {
            var jobDirectory = ResolveTrustedJenkinsJobDirectory(jenkinsHome, jobName);
            if (jobDirectory is null)
            {
                diagnostic = $"Jenkins archived artifact job directory was not found or is not readable under {Path.Combine(jenkinsHome, "jobs", jobName)}.";
                return string.Empty;
            }

            var buildsRoot = ResolveTrustedReportPath(Path.Combine(jobDirectory.FullName, "builds"), jenkinsHome);
            if (!TrustedDirectoryExists(buildsRoot))
            {
                diagnostic = $"Jenkins archived artifact builds directory was not found or is not readable at {buildsRoot}.";
                return string.Empty;
            }

            var searchedBuildCount = 0;
            foreach (var buildDirectory in new DirectoryInfo(buildsRoot)
                .EnumerateDirectories()
                .Select(directory => new
                {
                    Directory = directory,
                    BuildNumber = int.TryParse(directory.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var buildNumber)
                        ? buildNumber
                        : -1
                })
                .Where(item => item.BuildNumber >= 0)
                .OrderByDescending(item => item.BuildNumber))
            {
                searchedBuildCount++;
                foreach (var candidate in buildReportCandidates(Path.Combine(buildDirectory.Directory.FullName, "archive")))
                {
                    var trustedCandidate = ResolveTrustedReportPath(candidate, jenkinsHome);
                    if (TrustedFileExists(trustedCandidate))
                    {
                        return trustedCandidate;
                    }
                }
            }

            diagnostic = searchedBuildCount == 0
                ? $"Jenkins archived artifact builds directory has no numeric builds at {buildsRoot}."
                : $"No archived artifact matched the expected report paths under {buildsRoot}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            diagnostic = $"Jenkins archived artifact lookup failed under {jenkinsHome}: {ex.Message}";
            return string.Empty;
        }

        return string.Empty;
    }

    private static DirectoryInfo? ResolveTrustedJenkinsJobDirectory(string jenkinsHome, string jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName)
            || !string.Equals(ResolveSafeJenkinsJobName(jobName), jobName, StringComparison.Ordinal))
        {
            return null;
        }

        var jobsRoot = ResolveTrustedReportPath(Path.Combine(jenkinsHome, "jobs"), jenkinsHome);
        if (!TrustedDirectoryExists(jobsRoot))
        {
            return null;
        }

        foreach (var directory in new DirectoryInfo(jobsRoot).EnumerateDirectories())
        {
            if (!string.Equals(directory.Name, jobName, StringComparison.Ordinal)
                || !string.Equals(ResolveSafeJenkinsJobName(directory.Name), directory.Name, StringComparison.Ordinal))
            {
                continue;
            }

            var trustedJobRoot = ResolveTrustedReportPath(directory.FullName, jobsRoot);
            if (TrustedDirectoryExists(trustedJobRoot))
            {
                return new DirectoryInfo(trustedJobRoot);
            }
        }

        return null;
    }

    private static ResolvedReportPath ResolvePlaywrightReportPath(
        CodeQualitySecurityOptions options,
        CodeQualityApplicationOptions application,
        string environment,
        BuildDeploymentJenkinsOptions? jenkinsOptions)
    {
        var manifestPath = ResolveSystemHealthReportPath(options, application, environment, jenkinsOptions, manifest => manifest.PlaywrightResultsPath, "playwright");
        if (!string.IsNullOrWhiteSpace(manifestPath.Path))
        {
            return manifestPath;
        }

        var configuredPath = string.Equals(environment, DevelopmentEnvironment, StringComparison.Ordinal)
            ? application.DevPlaywrightReportPath
            : application.ProdPlaywrightReportPath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return new ResolvedReportPath(ResolveTrustedReportPath(configuredPath, options.JenkinsWorkspaceRoot), options.JenkinsWorkspaceRoot);
        }

        return ResolveJenkinsPlaywrightReportPath(options, application, environment, jenkinsOptions);
    }

    private static ResolvedReportPath ResolveJenkinsPlaywrightReportPath(
        CodeQualitySecurityOptions options,
        CodeQualityApplicationOptions application,
        string environment,
        BuildDeploymentJenkinsOptions? jenkinsOptions)
    {
        var jobName = ResolveJenkinsJobName(application, environment, jenkinsOptions);
        if (string.IsNullOrWhiteSpace(jobName))
        {
            return ResolvedReportPath.Empty;
        }

        var safeJobName = ResolveSafeJenkinsJobName(jobName);
        if (string.IsNullOrWhiteSpace(safeJobName))
        {
            return ResolvedReportPath.Empty;
        }

        var workspaceCandidates = BuildPlaywrightReportCandidates(
            Path.Combine(options.JenkinsWorkspaceRoot, safeJobName));
        foreach (var candidate in workspaceCandidates)
        {
            var trustedCandidate = ResolveTrustedReportPath(candidate, options.JenkinsWorkspaceRoot);
            if (TrustedFileExists(trustedCandidate))
            {
                return new ResolvedReportPath(trustedCandidate, options.JenkinsWorkspaceRoot);
            }
        }

        var jenkinsHome = ResolveJenkinsHome(options.JenkinsHomeRoot, options.JenkinsWorkspaceRoot);
        var archiveDiagnostic = string.Empty;
        if (!string.IsNullOrWhiteSpace(jenkinsHome))
        {
            var archivePath = ResolveLatestArchivedReportPath(jenkinsHome, safeJobName, BuildPlaywrightReportCandidates, out archiveDiagnostic);
            if (!string.IsNullOrWhiteSpace(archivePath))
            {
                return new ResolvedReportPath(archivePath, jenkinsHome);
            }
        }

        return new ResolvedReportPath(workspaceCandidates[0], options.JenkinsWorkspaceRoot, archiveDiagnostic);
    }

    private static ResolvedReportPath ResolveSystemHealthReportPath(
        CodeQualitySecurityOptions options,
        CodeQualityApplicationOptions application,
        string environment,
        BuildDeploymentJenkinsOptions? jenkinsOptions,
        Func<DashboardManifest, string> topLevelPathSelector,
        params string[] reportKeys)
    {
        var dashboardRoot = ResolvePublishedDashboardRoot(options, application, environment, jenkinsOptions);
        if (string.IsNullOrWhiteSpace(dashboardRoot))
        {
            return ResolvedReportPath.Empty;
        }

        var manifest = LoadDashboardManifest(dashboardRoot);
        if (manifest is null)
        {
            return ResolvedReportPath.Empty;
        }

        try
        {
            var topLevelPath = topLevelPathSelector(manifest);
            if (!string.IsNullOrWhiteSpace(topLevelPath))
            {
                var resolvedPath = ResolveDashboardRelativePath(topLevelPath, dashboardRoot);
                return string.IsNullOrWhiteSpace(resolvedPath)
                    ? ResolvedReportPath.Empty
                    : new ResolvedReportPath(resolvedPath, dashboardRoot);
            }

            foreach (var reportKey in reportKeys)
            {
                if (manifest.Reports.TryGetValue(reportKey, out var relativePath)
                    && !string.IsNullOrWhiteSpace(relativePath))
                {
                    var resolvedPath = ResolveDashboardRelativePath(relativePath, dashboardRoot);
                    return string.IsNullOrWhiteSpace(resolvedPath)
                        ? ResolvedReportPath.Empty
                        : new ResolvedReportPath(resolvedPath, dashboardRoot);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ResolvedReportPath.Empty;
        }

        return ResolvedReportPath.Empty;
    }

    private static string ResolvePublishedDashboardRoot(
        CodeQualitySecurityOptions options,
        CodeQualityApplicationOptions application,
        string environment,
        BuildDeploymentJenkinsOptions? jenkinsOptions)
    {
        var jobName = ResolveJenkinsJobName(application, environment, jenkinsOptions);
        if (string.IsNullOrWhiteSpace(jobName))
        {
            return string.Empty;
        }

        var safeJobName = ResolveSafeJenkinsJobName(jobName);
        if (string.IsNullOrWhiteSpace(safeJobName))
        {
            return string.Empty;
        }

        var dashboardBaseRoot = ResolveTrustedRoot(options.SystemHealthArtifactRoot);
        if (string.IsNullOrWhiteSpace(dashboardBaseRoot))
        {
            return string.Empty;
        }

        return ResolveTrustedReportPath(
            Path.Combine(dashboardBaseRoot, safeJobName, "latest"),
            dashboardBaseRoot);
    }

    private static DashboardManifest? LoadDashboardManifest(string dashboardRoot)
    {
        var manifestPath = Path.Combine(dashboardRoot, "manifest.json");
        if (!TrustedFileExists(manifestPath))
        {
            return null;
        }

        using var document = JsonDocument.Parse(ReadTrustedText(manifestPath));
        var manifest = new DashboardManifest
        {
            BuildNumber = GetString(document.RootElement, "buildNumber"),
            Commit = GetString(document.RootElement, "commit"),
            PlaywrightResultsPath = GetString(document.RootElement, "playwrightResultsPath"),
            CodeAnalysisPath = GetString(document.RootElement, "codeAnalysisPath"),
            DependencyCheckPath = GetString(document.RootElement, "dependencyCheckPath"),
            CycloneDxBomPath = GetString(document.RootElement, "cycloneDxBomPath"),
            LintReportPath = GetString(document.RootElement, "lintReportPath")
        };

        if (document.RootElement.TryGetProperty("reports", out var reportsElement)
            && reportsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in reportsElement.EnumerateObject())
            {
                manifest.Reports[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return manifest;
    }

    private static string ResolveDashboardRelativePath(string relativePath, string dashboardRoot)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return string.Empty;
        }

        return ResolveTrustedReportPath(Path.Combine(dashboardRoot, relativePath), dashboardRoot);
    }

    private static bool AiCodeAnalysisHasZeroFindings(string reportPath, string trustedRoot)
    {
        try
        {
            var fullReportPath = ResolveTrustedReportPath(reportPath, trustedRoot);
            if (!TrustedFileExists(fullReportPath))
            {
                return false;
            }

            using var document = JsonDocument.Parse(ReadTrustedText(fullReportPath));
            if (!document.RootElement.TryGetProperty("findings", out var findingsElement)
                || findingsElement.ValueKind != JsonValueKind.Array)
            {
                return document.RootElement.TryGetProperty("totalFindings", out var totalFindingsElement)
                    && totalFindingsElement.TryGetInt32(out var totalFindings)
                    && totalFindings == 0;
            }

            return !findingsElement.EnumerateArray().Any();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string ResolveTrustedReportPath(string reportPath, string trustedRoot)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            return string.Empty;
        }

        var expandedRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(trustedRoot))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var expandedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(reportPath));
        if (!expandedPath.StartsWith(expandedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Report path is outside the trusted System Health artifact root: {expandedPath}");
        }

        return expandedPath;
    }

    private static bool TrustedFileExists(string trustedPath)
    {
        return Path.IsPathFullyQualified(trustedPath) && new FileInfo(trustedPath).Exists;
    }

    private static bool TrustedDirectoryExists(string trustedPath)
    {
        return Path.IsPathFullyQualified(trustedPath) && new DirectoryInfo(trustedPath).Exists;
    }

    private static string ReadTrustedText(string trustedPath)
    {
        if (!Path.IsPathFullyQualified(trustedPath))
        {
            throw new InvalidOperationException($"Trusted path must be fully qualified: {trustedPath}");
        }

        using var reader = new FileInfo(trustedPath).OpenText();
        return reader.ReadToEnd();
    }

    private static bool HasProviderSuccess(
        IEnumerable<CodeQualityProviderStatusDto> providerStatuses,
        string provider)
    {
        return providerStatuses.Any(status =>
            string.Equals(status.Provider, provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(status.Status, ProviderSuccessStatus, StringComparison.OrdinalIgnoreCase));
    }

    private static SonarMetricDto CreateZeroSonarMetric(string key, string label)
    {
        return new SonarMetricDto
        {
            Key = key,
            Label = label,
            Value = "0",
            BestValue = true
        };
    }

    private static SonarMetricDto[] ReconcileSonarIssueMetrics(
        SonarMetricDto[] metrics,
        string sonarComponent,
        int vulnerabilityTotalCount,
        int bugTotalCount,
        int codeSmellTotalCount,
        List<string> warnings)
    {
        if (metrics.Length == 0)
        {
            return metrics;
        }

        var reconciled = metrics
            .Select(metric => new SonarMetricDto
            {
                Key = metric.Key,
                Label = metric.Label,
                Value = metric.Value,
                BestValue = metric.BestValue
            })
            .ToArray();

        ReconcileSonarIssueMetric(reconciled, sonarComponent, "vulnerabilities", "Vulnerabilities", vulnerabilityTotalCount, warnings);
        ReconcileSonarIssueMetric(reconciled, sonarComponent, "bugs", "Bugs", bugTotalCount, warnings);
        ReconcileSonarIssueMetric(reconciled, sonarComponent, "code_smells", "Code Smells", codeSmellTotalCount, warnings);

        return reconciled;
    }

    private static void ReconcileSonarIssueMetric(
        SonarMetricDto[] metrics,
        string sonarComponent,
        string key,
        string label,
        int issueTotalCount,
        List<string> warnings)
    {
        var metric = metrics.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
        if (metric is null || !int.TryParse(metric.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var measureTotalCount))
        {
            return;
        }

        if (measureTotalCount <= issueTotalCount)
        {
            return;
        }

        metric.Value = issueTotalCount.ToString(CultureInfo.InvariantCulture);
        metric.BestValue = issueTotalCount == 0;
        metric.Label = string.IsNullOrWhiteSpace(metric.Label) ? label : metric.Label;
        warnings.Add($"SonarQube {label} measure for {sonarComponent} reported {measureTotalCount}, but open issue search reported {issueTotalCount}. Displaying open issue search totals until SonarQube re-indexes measures.");
    }

    private static string ResolveGitHubOrganization(
        CodeQualitySecurityOptions options,
        CodeQualityApplicationOptions application)
    {
        return FirstConfigured(application.GitHubOrganization, options.GitHubOrganization);
    }

    private static bool IsRepositoryMissing(
        CodeQualityApplicationOptions application,
        string environment,
        string gitHubRepository)
    {
        return string.Equals(environment, ProductionEnvironment, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(application.DevGitHubRepository)
            && string.IsNullOrWhiteSpace(application.ProdGitHubRepository)
            && string.IsNullOrWhiteSpace(gitHubRepository);
    }

    private static string FirstConfigured(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static bool HasCriticalProductionAlert(
        List<string> warnings,
        SonarSnapshotData sonarData,
        GitHubSecurityData gitHubData,
        DependencyCheckReportData dependencyCheckData,
        PlaywrightReportData playwrightData)
    {
        return warnings.Count > 0
            || HasFailedQualityGate(sonarData.Metrics)
            || sonarData.Vulnerabilities.Any(issue => IsSeverity(issue.Severity, CriticalSeverity))
            || sonarData.Bugs.Any(issue => IsSeverity(issue.Severity, "BLOCKER", CriticalSeverity))
            || sonarData.CodeSmells.Any(issue => IsSeverity(issue.Severity, "BLOCKER", CriticalSeverity))
            || gitHubData.SeverityCounts.Any(count => IsSeverity(count.Severity, CriticalSeverity) && count.Count > 0)
            || gitHubData.CodeScanningSeverityCounts.Any(count => IsSeverity(count.Severity, CriticalSeverity) && count.Count > 0)
            || gitHubData.SecretScanningCounts.Any(count => string.Equals(count.Key, "OPEN", StringComparison.OrdinalIgnoreCase) && count.Count > 0)
            || dependencyCheckData.SeverityCounts.Any(count => IsSeverity(count.Severity, CriticalSeverity) && count.Count > 0)
            || playwrightData.FailedTests > 0;
    }

    private static bool HasFailedQualityGate(IEnumerable<SonarMetricDto> metrics)
    {
        return metrics.Any(metric =>
            IsSeverity(metric.Key, "alert_status", "quality_gate_status", "quality_gate")
            && IsSeverity(metric.Value, "ERROR", "FAILED", "FAILURE"));
    }

    private static bool IsSeverity(string value, params string[] expectedValues)
    {
        return expectedValues.Any(expected => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildStatusDetail(
        List<string> warnings,
        bool hasSecurityWarning,
        bool hasProductionCriticalAlert,
        CodeQualityApplicationOptions application,
        string environment)
    {
        if (warnings.Count > 0)
        {
            return string.Join(" ", warnings);
        }

        if (hasProductionCriticalAlert)
        {
            return $"{BuildApplicationEnvironmentLabel(application, environment)} Code Quality & Security critical alerts require immediate attention.";
        }

        return hasSecurityWarning
            ? "Code quality or security findings need review."
            : "Code quality and security data loaded.";
    }

    private static string BuildApplicationEnvironmentLabel(CodeQualityApplicationOptions application, string environment)
    {
        var applicationLabel = FirstConfigured(application.Label, application.Key, "Selected application");
        return $"{applicationLabel} {environment}";
    }

    private static string BuildSnapshotStatus(
        List<string> warnings,
        bool hasSecurityWarning,
        bool hasProductionCriticalAlert)
    {
        if (hasProductionCriticalAlert)
        {
            return CriticalStatus;
        }

        return warnings.Count > 0 || hasSecurityWarning ? WarningStatus : HealthyStatus;
    }

    private static CodeQualitySecuritySnapshotDto CreateWarningSnapshot(
        IReadOnlyList<CodeQualityApplicationOptionDto> applications,
        IReadOnlyList<string> environments,
        string selectedApplicationKey,
        string selectedEnvironment,
        CodeQualitySecurityOptions options,
        string detail)
    {
        return new CodeQualitySecuritySnapshotDto
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Status = BuildUnavailableEnvironmentStatus(selectedEnvironment),
            StatusDetail = detail,
            SelectedApplicationKey = selectedApplicationKey,
            SelectedEnvironment = selectedEnvironment,
            Applications = applications,
            Environments = environments,
            GitHubDashboardUrl = options.GitHubDashboardUrl,
            GitHubSeverityCounts = GitHubSecurityData.Empty.SeverityCounts,
            GitHubCodeScanningSeverityCounts = GitHubCodeScanningData.Empty.SeverityCounts,
            GitHubSecretScanningCounts = GitHubSecretScanningData.Empty.Counts,
            DependencyCheckStatus = DependencyCheckReportData.Empty.Status,
            DependencyCheckStatusDetail = DependencyCheckReportData.Empty.StatusDetail,
            DependencyCheckSeverityCounts = DependencyCheckReportData.Empty.SeverityCounts,
            CycloneDxStatus = CycloneDxBomData.Empty.Status,
            CycloneDxStatusDetail = CycloneDxBomData.Empty.StatusDetail,
            CycloneDxComponents = CycloneDxBomData.Empty.Components,
            PlaywrightStatus = PlaywrightReportData.Empty.Status,
            PlaywrightStatusDetail = PlaywrightReportData.Empty.StatusDetail,
            PlaywrightWorkflowContracts = PlaywrightReportData.Empty.WorkflowContracts,
            ProviderStatuses =
            [
                new CodeQualityProviderStatusDto
                {
                    Provider = "Configuration",
                    Status = ProviderUnavailableStatus,
                    Detail = detail,
                    CheckedAtUtc = DateTime.UtcNow
                }
            ]
        };
    }

    private static string BuildUnavailableEnvironmentStatus(string selectedEnvironment)
    {
        return string.Equals(selectedEnvironment, ProductionEnvironment, StringComparison.OrdinalIgnoreCase)
            ? CriticalStatus
            : WarningStatus;
    }

    private static DependencyCheckReportData ParseDependencyCheckReport(JsonElement root, string reportPath)
    {
        var projectInfo = GetProperty(root, "projectInfo");
        var scanInfo = GetProperty(root, "scanInfo");
        var findings = new List<DependencyCheckFindingDto>();
        var packagePaths = new List<string>();
        var dependenciesScanned = 0;

        if (TryGetArray(root, "dependencies", out var dependencies))
        {
            foreach (var dependency in dependencies.EnumerateArray())
            {
                dependenciesScanned++;
                var filePath = GetString(dependency, "filePath");
                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    packagePaths.Add(Path.GetFullPath(Environment.ExpandEnvironmentVariables(filePath)));
                }

                if (!TryGetArray(dependency, "vulnerabilities", out var vulnerabilities))
                {
                    continue;
                }

                foreach (var vulnerability in vulnerabilities.EnumerateArray())
                {
                    findings.Add(CreateDependencyCheckFinding(dependency, vulnerability));
                }
            }
        }

        var severityCounts = CreateDependencyCheckSeverityCounts(findings);
        var status = findings.Count == 0 ? HealthyStatus : WarningStatus;
        return new DependencyCheckReportData(
            status,
            findings.Count == 0
                ? "OWASP Dependency-Check report loaded with no vulnerabilities."
                : $"OWASP Dependency-Check report loaded with {findings.Count} vulnerabilities.",
            GetString(projectInfo, "name"),
            GetString(scanInfo, "engineVersion"),
            reportPath,
            ParseDateTime(GetString(projectInfo, "reportDate")),
            dependenciesScanned,
            findings.Count,
            severityCounts,
            findings.Take(MaximumDependencyCheckFindings).ToArray(),
            packagePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static CycloneDxBomData ParseCycloneDxBom(JsonElement root, string bomPath)
    {
        var bomFormat = GetString(root, "bomFormat");
        var specVersion = GetString(root, "specVersion");
        var serialNumber = GetString(root, "serialNumber");
        var metadata = GetProperty(root, "metadata");
        var generatedAtUtc = ParseDateTime(GetString(metadata, "timestamp"));
        var components = new List<CycloneDxComponentDto>();

        if (TryGetArray(root, "components", out var componentElements))
        {
            foreach (var component in componentElements.EnumerateArray())
            {
                components.Add(new CycloneDxComponentDto
                {
                    Type = GetString(component, "type"),
                    Name = GetString(component, "name"),
                    Version = GetString(component, "version"),
                    PackageUrl = GetString(component, "purl"),
                    BomRef = GetString(component, "bom-ref"),
                    Scope = GetString(component, "scope")
                });
            }
        }

        var validationMessages = new List<string>();
        if (!string.Equals(bomFormat, "CycloneDX", StringComparison.OrdinalIgnoreCase))
        {
            validationMessages.Add($"BOM format is '{DisplayValue(bomFormat)}' but expected 'CycloneDX'");
        }

        if (components.Count == 0)
        {
            validationMessages.Add("no components were found");
        }

        var status = validationMessages.Count == 0 ? HealthyStatus : WarningStatus;
        var statusDetail = validationMessages.Count == 0
            ? $"CycloneDX SBOM loaded with {components.Count} components."
            : $"CycloneDX SBOM loaded, but validation failed: {string.Join("; ", validationMessages)}.";

        return new CycloneDxBomData(
            status,
            statusDetail,
            bomPath,
            bomFormat,
            specVersion,
            serialNumber,
            generatedAtUtc,
            components.Count,
            components.Take(MaximumCycloneDxComponents).ToArray());
    }

    private static DependencyCheckReportData ApplyDependencyCheckTrustChecks(
        DependencyCheckReportData data,
        CodeQualityApplicationOptions application,
        string environment,
        string reportPath,
        string trustedScanRoot)
    {
        var validationMessages = new List<string>();
        var expectedProjectName = application.DependencyCheckExpectedProjectName;
        if (!string.IsNullOrWhiteSpace(expectedProjectName)
            && !string.Equals(data.ProjectName, expectedProjectName, StringComparison.OrdinalIgnoreCase))
        {
            validationMessages.Add($"project name is '{DisplayValue(data.ProjectName)}' but expected '{expectedProjectName}'");
        }

        if (application.DependencyCheckMinimumDependenciesScanned > 0
            && data.DependenciesScanned < application.DependencyCheckMinimumDependenciesScanned)
        {
            validationMessages.Add($"only {data.DependenciesScanned} dependencies were scanned; expected at least {application.DependencyCheckMinimumDependenciesScanned}");
        }

        if (application.DependencyCheckMaximumReportAgeHours > 0)
        {
            if (data.ReportDateUtc is null)
            {
                validationMessages.Add("report date is missing or invalid");
            }
            else
            {
                var reportAge = DateTime.UtcNow - data.ReportDateUtc.Value;
                if (reportAge > TimeSpan.FromHours(application.DependencyCheckMaximumReportAgeHours))
                {
                    validationMessages.Add($"report is stale at {Math.Floor(reportAge.TotalHours)} hours old; maximum is {application.DependencyCheckMaximumReportAgeHours} hours");
                }
            }
        }

        if (HasDependencyCheckTrustConfiguration(application, environment))
        {
            ValidateDependencyCheckReportPath(data, reportPath, validationMessages);
            ValidateExpectedDependencyCheckScanFiles(data, application, environment, trustedScanRoot, validationMessages);
        }

        if (validationMessages.Count == 0)
        {
            return data;
        }

        var vulnerabilityDetail = data.VulnerabilityCount == 0
            ? "OWASP Dependency-Check report loaded with no vulnerabilities"
            : $"OWASP Dependency-Check report loaded with {data.VulnerabilityCount} vulnerabilities";
        return data with
        {
            Status = WarningStatus,
            StatusDetail = $"{vulnerabilityDetail}, but scan trust checks failed: {string.Join("; ", validationMessages)}."
        };
    }

    private static bool IsDependencyCheckTrustWarning(DependencyCheckReportData data)
    {
        return string.Equals(data.Status, WarningStatus, StringComparison.OrdinalIgnoreCase)
            && data.StatusDetail.Contains("scan trust checks failed", StringComparison.OrdinalIgnoreCase);
    }

    private static DependencyCheckReportData SuppressUntrustedDependencyCheckFindings(DependencyCheckReportData data)
    {
        return data with
        {
            VulnerabilityCount = 0,
            SeverityCounts = DependencyCheckReportData.Empty.SeverityCounts,
            Findings = Array.Empty<DependencyCheckFindingDto>()
        };
    }

    private static void ValidateDependencyCheckReportPath(
        DependencyCheckReportData data,
        string reportPath,
        List<string> validationMessages)
    {
        if (!string.Equals(data.ReportPath, reportPath, StringComparison.OrdinalIgnoreCase))
        {
            validationMessages.Add("resolved report path changed during load");
        }
    }

    private static bool HasDependencyCheckTrustConfiguration(CodeQualityApplicationOptions application, string environment)
    {
        return !string.IsNullOrWhiteSpace(application.DependencyCheckExpectedProjectName)
            || ResolveDependencyCheckExpectedScanFiles(application, environment).Any(path => !string.IsNullOrWhiteSpace(path))
            || application.DependencyCheckMinimumDependenciesScanned > 0
            || application.DependencyCheckMaximumReportAgeHours > 0
            || application.DependencyCheckRequirePackageLock
            || application.DependencyCheckRequireNodeModules;
    }

    private static string[] ResolveDependencyCheckExpectedScanFiles(CodeQualityApplicationOptions application, string environment)
    {
        var environmentFiles = string.Equals(environment, DevelopmentEnvironment, StringComparison.Ordinal)
            ? application.DevDependencyCheckExpectedScanFiles
            : application.ProdDependencyCheckExpectedScanFiles;

        return environmentFiles.Any(path => !string.IsNullOrWhiteSpace(path))
            ? environmentFiles
            : application.DependencyCheckExpectedScanFiles;
    }

    private static void ValidateExpectedDependencyCheckScanFiles(
        DependencyCheckReportData data,
        CodeQualityApplicationOptions application,
        string environment,
        string trustedScanRoot,
        List<string> validationMessages)
    {
        foreach (var expectedPath in ResolveDependencyCheckExpectedScanFiles(application, environment).Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var fullExpectedPath = ResolveTrustedReportPath(expectedPath, trustedScanRoot);
            if (!data.PackagePaths.Any(path => string.Equals(path, fullExpectedPath, StringComparison.OrdinalIgnoreCase)))
            {
                validationMessages.Add($"expected scan file is missing from report: '{fullExpectedPath}'");
                continue;
            }

            if (application.DependencyCheckRequirePackageLock
                && string.Equals(Path.GetFileName(fullExpectedPath), "package.json", StringComparison.OrdinalIgnoreCase)
                && PackageJsonContainsDependencies(fullExpectedPath)
                && !HasPackageLockFile(fullExpectedPath))
            {
                validationMessages.Add($"package manifest has dependencies but no lock file beside '{fullExpectedPath}'");
            }

            if (application.DependencyCheckRequireNodeModules
                && string.Equals(Path.GetFileName(fullExpectedPath), "package.json", StringComparison.OrdinalIgnoreCase)
                && PackageJsonContainsDependencies(fullExpectedPath)
                && !TrustedDirectoryExists(ResolveTrustedReportPath(
                    Path.Combine(Path.GetDirectoryName(fullExpectedPath) ?? trustedScanRoot, "node_modules"),
                    trustedScanRoot)))
            {
                validationMessages.Add($"package manifest has dependencies but node_modules is missing beside '{fullExpectedPath}'");
            }
        }
    }

    private static bool PackageJsonContainsDependencies(string packageJsonPath)
    {
        try
        {
            using var document = JsonDocument.Parse(ReadTrustedText(packageJsonPath));
            return JsonObjectPropertyHasValues(document.RootElement, "dependencies")
                || JsonObjectPropertyHasValues(document.RootElement, "devDependencies")
                || JsonObjectPropertyHasValues(document.RootElement, "peerDependencies")
                || JsonObjectPropertyHasValues(document.RootElement, "optionalDependencies");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    private static bool JsonObjectPropertyHasValues(JsonElement element, string propertyName)
    {
        var property = GetProperty(element, propertyName);
        return property.ValueKind == JsonValueKind.Object && property.EnumerateObject().Any();
    }

    private static bool HasPackageLockFile(string packageJsonPath)
    {
        var directory = Path.GetDirectoryName(packageJsonPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        return TrustedFileExists(ResolveTrustedReportPath(Path.Combine(directory, "package-lock.json"), directory))
            || TrustedFileExists(ResolveTrustedReportPath(Path.Combine(directory, "npm-shrinkwrap.json"), directory))
            || TrustedFileExists(ResolveTrustedReportPath(Path.Combine(directory, "yarn.lock"), directory))
            || TrustedFileExists(ResolveTrustedReportPath(Path.Combine(directory, "pnpm-lock.yaml"), directory));
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "missing" : value;
    }

    private static DependencyCheckFindingDto CreateDependencyCheckFinding(JsonElement dependency, JsonElement vulnerability)
    {
        var severity = NormalizeDependencyCheckSeverity(GetString(vulnerability, "severity"));
        return new DependencyCheckFindingDto
        {
            Severity = severity,
            Identifier = FirstConfigured(GetString(vulnerability, "name"), GetString(vulnerability, "source")),
            Dependency = FirstConfigured(GetString(dependency, "fileName"), GetString(dependency, "filePath")),
            PackagePath = GetString(dependency, "filePath"),
            Summary = FirstConfigured(GetString(vulnerability, "description"), GetString(vulnerability, "title")),
            CvssScore = GetCvssScore(vulnerability),
            Url = GetFirstReferenceUrl(vulnerability)
        };
    }

    private static DependencyCheckSeverityCountDto[] CreateDependencyCheckSeverityCounts(IEnumerable<DependencyCheckFindingDto> findings)
    {
        var counts = DependencyCheckSeverities.ToDictionary(severity => severity, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var finding in findings)
        {
            var severity = NormalizeDependencyCheckSeverity(finding.Severity);
            counts[severity] = counts.GetValueOrDefault(severity) + 1;
        }

        return DependencyCheckSeverities
            .Select(severity => new DependencyCheckSeverityCountDto { Severity = severity, Count = counts[severity] })
            .ToArray();
    }

    private static string NormalizeDependencyCheckSeverity(string severity)
    {
        return severity.ToUpperInvariant() switch
        {
            CriticalSeverity => "CRITICAL",
            "HIGH" => "HIGH",
            "MEDIUM" or "MODERATE" => "MEDIUM",
            "LOW" => "LOW",
            _ => "UNKNOWN"
        };
    }

    private static double? GetCvssScore(JsonElement vulnerability)
    {
        foreach (var propertyName in new[] { "cvssv4", "cvssv3", "cvssv2" })
        {
            var cvss = GetProperty(vulnerability, propertyName);
            if (cvss.ValueKind == JsonValueKind.Object
                && TryGetDouble(cvss, "baseScore", out var score))
            {
                return score;
            }
        }

        return null;
    }

    private static string GetFirstReferenceUrl(JsonElement vulnerability)
    {
        if (!TryGetArray(vulnerability, "references", out var references))
        {
            return string.Empty;
        }

        foreach (var reference in references.EnumerateArray())
        {
            var url = GetString(reference, "url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return string.Empty;
    }

    private static JsonElement GetProperty(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property)
            ? property
            : default;
    }

    private static bool TryGetArray(JsonElement element, string propertyName, out JsonElement array)
    {
        array = GetProperty(element, propertyName);
        return array.ValueKind == JsonValueKind.Array;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        var property = GetProperty(element, propertyName);
        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : string.Empty;
    }

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        var property = GetProperty(element, propertyName);
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.String
            && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static DateTime? ParseDateTime(string value)
    {
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var date)
            ? date.ToUniversalTime()
            : null;
    }



    private static LintReportData ApplyLintTrustChecks(
        LintReportData data,
        CodeQualityApplicationOptions application,
        string environment,
        string expectedBranch,
        string expectedCommit)
    {
        var validationMessages = new List<string>();

        if (!IsLintApplicationMatch(data.Application, application))
        {
            validationMessages.Add($"application '{DisplayValue(data.Application)}' did not match selected application '{DisplayValue(application.Label)}'");
        }

        if (string.IsNullOrWhiteSpace(data.Environment)
            || !string.Equals(ResolveEnvironment(data.Environment), environment, StringComparison.OrdinalIgnoreCase))
        {
            validationMessages.Add($"environment '{DisplayValue(data.Environment)}' did not match selected environment '{environment}'");
        }

        if (!string.IsNullOrWhiteSpace(expectedBranch)
            && (string.IsNullOrWhiteSpace(data.Branch) || !string.Equals(data.Branch, expectedBranch, StringComparison.OrdinalIgnoreCase)))
        {
            validationMessages.Add($"branch '{DisplayValue(data.Branch)}' did not match expected branch '{expectedBranch}'");
        }

        if (!string.IsNullOrWhiteSpace(expectedCommit)
            && (string.IsNullOrWhiteSpace(data.Commit) || !IsSameCommit(data.Commit, expectedCommit)))
        {
            validationMessages.Add($"commit '{ShortRevision(DisplayValue(data.Commit))}' did not match expected branch HEAD '{ShortRevision(expectedCommit)}'");
        }

        if (!data.GeneratedAtUtc.HasValue)
        {
            validationMessages.Add("generatedAtUtc was missing or invalid");
        }
        else if (application.LintMaximumReportAgeHours > 0
            && data.GeneratedAtUtc.Value.ToUniversalTime() < DateTime.UtcNow.AddHours(-application.LintMaximumReportAgeHours))
        {
            validationMessages.Add($"generatedAtUtc '{data.GeneratedAtUtc.Value:O}' was older than {application.LintMaximumReportAgeHours} hour(s)");
        }

        if (validationMessages.Count == 0)
        {
            return data with { IsTrusted = true };
        }

        return data with
        {
            Status = WarningStatus,
            StatusDetail = $"Lint & Standards report is untrusted: {string.Join("; ", validationMessages)}.",
            IsTrusted = false
        };
    }

    private static bool IsLintApplicationMatch(string reportApplication, CodeQualityApplicationOptions application)
    {
        if (string.IsNullOrWhiteSpace(reportApplication))
        {
            return false;
        }

        var reportValue = NormalizeProviderIdentity(reportApplication);
        var acceptedValues = new[]
        {
            application.Key,
            application.Label,
            application.DevGitHubRepository,
            application.ProdGitHubRepository
        };

        return acceptedValues
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeProviderIdentity)
            .Any(value => string.Equals(value, reportValue, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeProviderIdentity(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static LintReportData SuppressUntrustedLintFindings(LintReportData data)
    {
        return data with
        {
            TotalFindings = 0,
            ErrorCount = 0,
            WarningCount = 0,
            ToolsTotal = 0,
            ToolsPassed = 0,
            ToolsFailed = 0,
            ToolsNotApplicable = 0,
            ToolsNotConfigured = 0,
            Tools = Array.Empty<LintToolResultDto>(),
            Findings = Array.Empty<LintFindingDto>()
        };
    }
    private static LintReportData ParseLintReport(JsonElement root)
    {
        var tools = ApplyMandatoryLintToolContract(ParseLintTools(root));
        var findings = ParseLintFindings(root);
        var toolsTotal = tools.Length;
        var toolsFailed = tools.Count(IsFailedLintTool);
        var toolsPassed = tools.Count(tool => string.Equals(tool.Status, HealthyStatus, StringComparison.OrdinalIgnoreCase));
        var toolsNotApplicable = tools.Count(tool => string.Equals(tool.Status, NotApplicableStatus, StringComparison.OrdinalIgnoreCase));
        var toolsNotConfigured = tools.Count(tool => string.Equals(tool.Status, NotConfiguredStatus, StringComparison.OrdinalIgnoreCase));
        var errorCount = GetInt(root, "errorCount", tools.Sum(tool => tool.Errors));
        var warningCount = GetInt(root, "warningCount", tools.Sum(tool => tool.Warnings));
        var totalFindings = GetInt(root, "totalFindings", findings.Length);
        var hasStandardsGap = toolsNotConfigured > 0 || toolsFailed > 0 || errorCount > 0 || warningCount > 0;
        var reportStatus = GetString(root, StatusJsonProperty);
        var status = hasStandardsGap ? WarningStatus : FirstConfigured(reportStatus, HealthyStatus);
        var derivedStatusDetail = BuildLintStatusDetail(toolsFailed, toolsNotConfigured, warningCount);
        var statusDetail = hasStandardsGap && !IsFailedLintToolStatus(reportStatus)
            ? derivedStatusDetail
            : FirstConfigured(GetString(root, "statusDetail"), derivedStatusDetail);

        return new LintReportData(
            status,
            statusDetail,
            ParseDateTime(GetString(root, "generatedAtUtc")),
            totalFindings,
            errorCount,
            warningCount,
            toolsTotal,
            toolsPassed,
            toolsFailed,
            toolsNotApplicable,
            toolsNotConfigured,
            GetString(root, "application"),
            GetString(root, "environment"),
            GetString(root, "build"),
            GetString(root, "commit"),
            GetString(root, "branch"),
            true,
            tools,
            findings.Take(MaximumLintFindings).ToArray());
    }

    private static LintToolResultDto[] ParseLintTools(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var toolsElement) || toolsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<LintToolResultDto>();
        }

        return toolsElement.EnumerateArray()
            .Where(tool => tool.ValueKind == JsonValueKind.Object)
            .Select(tool => new LintToolResultDto
            {
                Name = GetString(tool, "name"),
                Category = GetString(tool, "category"),
                Status = FirstConfigured(GetString(tool, "status"), HealthyStatus),
                Errors = GetInt(tool, "errors"),
                Warnings = GetInt(tool, "warnings"),
                Detail = GetString(tool, "detail"),
                ExitCode = GetInt(tool, "exitCode")
            })
            .ToArray();
    }

    private static LintToolResultDto[] ApplyMandatoryLintToolContract(LintToolResultDto[] reportedTools)
    {
        var contractedTools = new List<LintToolResultDto>();
        var usedIndexes = new HashSet<int>();

        foreach (var category in MandatoryLintCategories)
        {
            var matchIndex = -1;
            for (var index = 0; index < reportedTools.Length; index++)
            {
                if (usedIndexes.Contains(index))
                {
                    continue;
                }

                if (IsLintToolCategoryMatch(reportedTools[index], category))
                {
                    matchIndex = index;
                    break;
                }
            }

            if (matchIndex < 0)
            {
                contractedTools.Add(CreateNotConfiguredLintTool(category));
                continue;
            }

            usedIndexes.Add(matchIndex);
            var tool = reportedTools[matchIndex];
            contractedTools.Add(new LintToolResultDto
            {
                Name = string.IsNullOrWhiteSpace(tool.Name) ? category : tool.Name,
                Category = string.IsNullOrWhiteSpace(tool.Category) ? category : tool.Category,
                Status = tool.Status,
                Errors = tool.Errors,
                Warnings = tool.Warnings,
                Detail = tool.Detail,
                ExitCode = tool.ExitCode
            });
        }

        return contractedTools.ToArray();
    }

    private static bool IsLintToolCategoryMatch(LintToolResultDto tool, string category)
    {
        if (string.Equals(tool.Category, category, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return category switch
        {
            "Build / Analyzers" => IsLintToolNamed(tool, ".NET Build / Analyzers", "Backend Build"),
            "Formatting" => IsLintToolNamed(tool, ".NET Format"),
            "Unit Tests" => IsLintToolNamed(tool, ".NET Tests", "Shell Unit / Standards", "Backend Coverage Tests", "Mobile Coverage Tests", "Backend / Mobile Tests"),
            "Static Lint" => IsLintToolNamed(tool, "Backend ESLint", "Mobile ESLint"),
            "Type Safety" => IsLintToolNamed(tool, "Shell Typecheck", "Mobile Type Check"),
            "Standards / Source Contracts" => IsLintToolNamed(tool, "CRM Standards", "Grid Backbone Duplication", "Tenant Template Terminology"),
            "Dependency Restore" => IsLintToolNamed(tool, "Backend Dependencies", "Mobile Dependencies"),
            _ => false
        };
    }

    private static bool IsLintToolNamed(LintToolResultDto tool, params string[] names)
    {
        return names.Any(name => string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static LintToolResultDto CreateNotConfiguredLintTool(string category)
    {
        return new LintToolResultDto
        {
            Name = category,
            Category = category,
            Status = NotConfiguredStatus,
            Errors = 0,
            Warnings = 0,
            Detail = $"{category} is not configured in the Phase 1 Lint & Standards producer.",
            ExitCode = 0
        };
    }

    private static bool IsFailedLintTool(LintToolResultDto tool)
    {
        return IsFailedLintToolStatus(tool.Status);
    }

    private static bool IsFailedLintToolStatus(string status)
    {
        return IsSeverity(status, WarningStatus, CriticalStatus, "Failed", "Failure", "Error");
    }

    private static string BuildLintStatusDetail(int toolsFailed, int toolsNotConfigured, int warningCount)
    {
        if (toolsFailed == 0 && toolsNotConfigured == 0 && warningCount == 0)
        {
            return "All mandatory Phase 1 lint and standards categories passed or were explicitly not applicable.";
        }

        var parts = new List<string>();
        if (toolsFailed > 0)
        {
            parts.Add($"{toolsFailed} mandatory Phase 1 lint and standards categor{(toolsFailed == 1 ? "y" : "ies")} failed");
        }

        if (toolsNotConfigured > 0)
        {
            parts.Add($"{toolsNotConfigured} mandatory Phase 1 lint and standards categor{(toolsNotConfigured == 1 ? "y is" : "ies are")} not configured");
        }

        if (warningCount > 0)
        {
            parts.Add($"{warningCount} warning finding{(warningCount == 1 ? string.Empty : "s")} reported");
        }

        return $"{string.Join("; ", parts)}.";
    }

    private static LintFindingDto[] ParseLintFindings(JsonElement root)
    {
        if (!root.TryGetProperty("findings", out var findingsElement) || findingsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<LintFindingDto>();
        }

        return findingsElement.EnumerateArray()
            .Where(finding => finding.ValueKind == JsonValueKind.Object)
            .Select(finding => new LintFindingDto
            {
                Tool = GetString(finding, "tool"),
                Severity = GetString(finding, "severity"),
                RuleId = GetString(finding, "ruleId"),
                File = GetString(finding, "file"),
                Line = GetNullableInt(finding, "line"),
                Column = GetNullableInt(finding, "column"),
                Message = GetString(finding, "message"),
                RawSummary = GetString(finding, "rawSummary")
            })
            .Where(finding => !string.IsNullOrWhiteSpace(finding.Message) || !string.IsNullOrWhiteSpace(finding.RawSummary))
            .ToArray();
    }

    private static bool IsLintWarning(LintReportData data)
    {
        return !string.Equals(data.Status, HealthyStatus, StringComparison.OrdinalIgnoreCase)
            || data.ErrorCount > 0
            || data.WarningCount > 0
            || data.ToolsFailed > 0
            || data.ToolsNotConfigured > 0;
    }
    private static PlaywrightReportData ParsePlaywrightReport(JsonElement root, CodeQualityApplicationOptions application)
    {
        var allResults = TryGetArray(root, "results", out var resultsElement)
            ? resultsElement.EnumerateArray().Select(CreatePlaywrightResult).ToArray()
            : Array.Empty<PlaywrightResultDto>();
        var displayResults = allResults.Take(MaximumPlaywrightResults).ToArray();
        var totalTests = GetInt(root, "totalTests", allResults.Length);
        var failedTests = GetInt(root, "failedTests", allResults.Count(result => IsSeverity(result.Status, "Failed", "Failure")));
        var passedTests = GetInt(root, "passedTests", allResults.Count(result => IsSeverity(result.Status, "Passed", "Success")));
        var skippedTests = GetInt(root, "skippedTests", allResults.Count(result => IsSeverity(result.Status, "Skipped")));
        var status = FirstConfigured(GetString(root, StatusJsonProperty), failedTests > 0 ? WarningStatus : HealthyStatus);
        var statusDetail = FirstConfigured(
            GetString(root, "statusDetail"),
            failedTests > 0
                ? $"Playwright completed with {failedTests} failed workflow check(s)."
                : "Playwright workflow checks passed.");

        return new PlaywrightReportData(
            status,
            statusDetail,
            GetString(root, "projectName"),
            GetString(root, "baseUrl"),
            ParseDateTime(GetString(root, "generatedAtUtc")),
            totalTests,
            passedTests,
            failedTests,
            skippedTests,
            GetDouble(root, "durationSeconds"),
            displayResults,
            BuildPlaywrightWorkflowContracts(allResults, application, GetString(root, "projectName")));
    }

    private static PlaywrightReportData ApplyPlaywrightTrustChecks(
        PlaywrightReportData data,
        CodeQualityApplicationOptions application)
    {
        var validationMessages = new List<string>();
        if (data.GeneratedAtUtc is null)
        {
            validationMessages.Add("generatedAtUtc is missing or invalid");
        }
        else if (application.PlaywrightMaximumReportAgeHours > 0)
        {
            var reportAge = DateTime.UtcNow - data.GeneratedAtUtc.Value;
            if (reportAge > TimeSpan.FromHours(application.PlaywrightMaximumReportAgeHours))
            {
                validationMessages.Add($"report is stale at {Math.Floor(reportAge.TotalHours)} hours old; maximum is {application.PlaywrightMaximumReportAgeHours} hours");
            }
        }

        if (validationMessages.Count == 0)
        {
            return data;
        }

        return data with
        {
            Status = WarningStatus,
            StatusDetail = $"Playwright report loaded, but provenance checks failed: {string.Join("; ", validationMessages)}."
        };
    }

    private static bool IsPlaywrightTrustWarning(PlaywrightReportData data)
    {
        return string.Equals(data.Status, WarningStatus, StringComparison.OrdinalIgnoreCase)
            && data.StatusDetail.Contains("provenance checks failed", StringComparison.OrdinalIgnoreCase);
    }

    private static PlaywrightReportData SuppressUntrustedPlaywrightResults(PlaywrightReportData data)
    {
        return data with
        {
            TotalTests = 0,
            PassedTests = 0,
            FailedTests = 0,
            SkippedTests = 0,
            DurationSeconds = 0,
            Results = Array.Empty<PlaywrightResultDto>(),
            WorkflowContracts = Array.Empty<PlaywrightWorkflowContractDto>()
        };
    }

    private static PlaywrightResultDto CreatePlaywrightResult(JsonElement element)
    {
        return new PlaywrightResultDto
        {
            Id = GetString(element, "id"),
            Scenario = GetString(element, "scenario"),
            Step = GetString(element, "step"),
            Browser = GetString(element, "browser"),
            Status = GetString(element, "status"),
            Duration = GetDouble(element, "duration"),
            Screenshot = GetString(element, "screenshot"),
            Error = GetString(element, "error")
        };
    }

    private static PlaywrightWorkflowContractDto[] BuildPlaywrightWorkflowContracts(
        IReadOnlyList<PlaywrightResultDto> results,
        CodeQualityApplicationOptions application,
        string projectName)
    {
        var definitions = ResolvePlaywrightWorkflowContractDefinitions(application, projectName);
        return definitions
            .Select(definition => CreatePlaywrightWorkflowContract(
                definition.Key,
                definition.Label,
                definition.DirectScenario,
                definition.DropdownScenario,
                definition.AuthenticatedScenario,
                results))
            .ToArray();
    }

    private static PlaywrightWorkflowContractDefinition[] ResolvePlaywrightWorkflowContractDefinitions(
        CodeQualityApplicationOptions application,
        string projectName)
    {
        if (string.Equals(application.Key, "fhx-map", StringComparison.OrdinalIgnoreCase)
            || projectName.Contains("FHX-MAP", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new(
                    "map-authenticated-login",
                    "FHX-MAP Authenticated Login",
                    "Login page",
                    "Login verification guard",
                    "Authenticated MAP login journey")
            ];
        }

        return
        [
            new(
                "free-mortgage-analysis",
                "Free Mortgage Analysis",
                "Login intent - Free Mortgage Analysis",
                "Login dropdown intent - Free Mortgage Analysis",
                "06 FMA authenticated analysis submit"),
            new(
                "loan-application",
                "Loan Application",
                "Login intent - Loan Application",
                "Login dropdown intent - Loan Application",
                "07 Loan Application handoff"),
            new(
                "client-home",
                "Client Home Page",
                "Login intent - Client Home Page",
                "Login dropdown intent - Client Home Page",
                "16 Valid 2FA completes login"),
            new(
                "map-course",
                "Mortgage Action Plan Course",
                "Login intent - Mortgage Action Plan Course",
                "Login dropdown intent - Mortgage Action Plan Course",
                "08 MAP Course authenticated route")
        ];
    }

    private static PlaywrightWorkflowContractDto CreatePlaywrightWorkflowContract(
        string key,
        string label,
        string directScenario,
        string dropdownScenario,
        string authenticatedScenario,
        IReadOnlyList<PlaywrightResultDto> results)
    {
        var directStatus = ResolvePlaywrightScenarioStatus(results, directScenario);
        var dropdownStatus = ResolvePlaywrightScenarioStatus(results, dropdownScenario);
        var authenticatedStatus = ResolvePlaywrightScenarioStatus(results, authenticatedScenario);
        var statuses = new[] { directStatus, dropdownStatus, authenticatedStatus };
        var status = ResolvePlaywrightWorkflowStatus(statuses);
        var detail = status switch
        {
            CriticalStatus => "At least one login intent workflow scenario failed.",
            WarningStatus => "One or more login intent workflow scenarios were not found in the Playwright report.",
            _ => "Direct login, dropdown login, and authenticated boundary passed."
        };

        return new PlaywrightWorkflowContractDto
        {
            Key = key,
            Label = label,
            DirectScenarioStatus = directStatus,
            DropdownScenarioStatus = dropdownStatus,
            AuthenticatedScenarioStatus = authenticatedStatus,
            Status = status,
            StatusDetail = detail
        };
    }

    private static string ResolvePlaywrightWorkflowStatus(IReadOnlyCollection<string> statuses)
    {
        if (statuses.Any(IsFailedPlaywrightStatus))
        {
            return CriticalStatus;
        }

        if (statuses.Any(IsMissingPlaywrightStatus))
        {
            return WarningStatus;
        }

        return HealthyStatus;
    }

    private static string ResolvePlaywrightScenarioStatus(IReadOnlyList<PlaywrightResultDto> results, string scenario)
    {
        var result = results.FirstOrDefault(item => string.Equals(item.Scenario, scenario, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(result?.Status) ? "Missing" : result.Status;
    }

    private static bool IsFailedPlaywrightStatus(string status)
    {
        return IsSeverity(status, "Failed", "Failure", CriticalStatus);
    }

    private static bool IsMissingPlaywrightStatus(string status)
    {
        return string.IsNullOrWhiteSpace(status) || string.Equals(status, "Missing", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetInt(JsonElement element, string propertyName, int fallback = 0)
    {
        var property = GetProperty(element, propertyName);
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
        {
            return value;
        }

        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        return fallback;
    }


    private static int? GetNullableInt(JsonElement element, string propertyName)
    {
        var property = GetProperty(element, propertyName);
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
        {
            return value;
        }

        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        return null;
    }
    private static double GetDouble(JsonElement element, string propertyName)
    {
        return TryGetDouble(element, propertyName, out var value) ? value : 0;
    }

    private sealed record SonarSnapshotData(
        SonarMetricDto[] Metrics,
        SonarIssueDto[] Vulnerabilities,
        SonarIssueDto[] Bugs,
        SonarIssueDto[] CodeSmells,
        int VulnerabilityTotalCount,
        int BugTotalCount,
        int CodeSmellTotalCount,
        DateTime? AnalysisDateUtc,
        string AnalysisRevision,
        string AnalysisVersion)
    {
        public static SonarSnapshotData Empty { get; } = new(
            Array.Empty<SonarMetricDto>(),
            Array.Empty<SonarIssueDto>(),
            Array.Empty<SonarIssueDto>(),
            Array.Empty<SonarIssueDto>(),
            0,
            0,
            0,
            null,
            string.Empty,
            string.Empty);
    }

    private sealed record ResolvedReportPath(string Path, string TrustedRoot, string Diagnostic = "")
    {
        public static ResolvedReportPath Empty { get; } = new(string.Empty, string.Empty);
    }

    private sealed record DependencyCheckReportData(
        string Status,
        string StatusDetail,
        string ProjectName,
        string EngineVersion,
        string ReportPath,
        DateTime? ReportDateUtc,
        int DependenciesScanned,
        int VulnerabilityCount,
        IReadOnlyList<DependencyCheckSeverityCountDto> SeverityCounts,
        IReadOnlyList<DependencyCheckFindingDto> Findings,
        IReadOnlyList<string> PackagePaths)
    {
        public static DependencyCheckReportData Empty { get; } = new(
            ProviderUnavailableStatus,
            "OWASP Dependency-Check report path is not configured.",
            string.Empty,
            string.Empty,
            string.Empty,
            null,
            0,
            0,
            CreateEmptySeverityCounts(),
            Array.Empty<DependencyCheckFindingDto>(),
            Array.Empty<string>());

        private static DependencyCheckSeverityCountDto[] CreateEmptySeverityCounts()
        {
            return DependencyCheckSeverities
                .Select(severity => new DependencyCheckSeverityCountDto { Severity = severity, Count = 0 })
                .ToArray();
        }
    }

    private sealed record CycloneDxBomData(
        string Status,
        string StatusDetail,
        string BomPath,
        string BomFormat,
        string SpecVersion,
        string SerialNumber,
        DateTime? GeneratedAtUtc,
        int ComponentCount,
        IReadOnlyList<CycloneDxComponentDto> Components)
    {
        public static CycloneDxBomData Empty { get; } = new(
            ProviderUnavailableStatus,
            "CycloneDX SBOM path is not configured.",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            null,
            0,
            Array.Empty<CycloneDxComponentDto>());
    }


    private sealed record LintReportLoadRequest(
        CodeQualityApplicationOptions Application,
        string Environment,
        string ExpectedBranch,
        string ExpectedCommit,
        BuildDeploymentJenkinsOptions? JenkinsOptions);


    private sealed record LintReportData(
        string Status,
        string StatusDetail,
        DateTime? GeneratedAtUtc,
        int TotalFindings,
        int ErrorCount,
        int WarningCount,
        int ToolsTotal,
        int ToolsPassed,
        int ToolsFailed,
        int ToolsNotApplicable,
        int ToolsNotConfigured,
        string Application,
        string Environment,
        string Build,
        string Commit,
        string Branch,
        bool IsTrusted,
        IReadOnlyList<LintToolResultDto> Tools,
        LintFindingDto[] Findings)
    {
        public static LintReportData Empty { get; } = new(
            ProviderUnavailableStatus,
            "Lint & Standards report path is not configured.",
            null,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            Array.Empty<LintToolResultDto>(),
            Array.Empty<LintFindingDto>());
    }
    private sealed record PlaywrightReportData(
        string Status,
        string StatusDetail,
        string ProjectName,
        string BaseUrl,
        DateTime? GeneratedAtUtc,
        int TotalTests,
        int PassedTests,
        int FailedTests,
        int SkippedTests,
        double DurationSeconds,
        IReadOnlyList<PlaywrightResultDto> Results,
        IReadOnlyList<PlaywrightWorkflowContractDto> WorkflowContracts)
    {
        public static PlaywrightReportData Empty { get; } = new(
            ProviderUnavailableStatus,
            "Playwright report path is not configured.",
            string.Empty,
            string.Empty,
            null,
            0,
            0,
            0,
            0,
            0,
            Array.Empty<PlaywrightResultDto>(),
            Array.Empty<PlaywrightWorkflowContractDto>());
    }

    private sealed record PlaywrightWorkflowContractDefinition(
        string Key,
        string Label,
        string DirectScenario,
        string DropdownScenario,
        string AuthenticatedScenario);

    private sealed class DashboardManifest
    {
        public string BuildNumber { get; set; } = string.Empty;
        public string Commit { get; set; } = string.Empty;
        public string PlaywrightResultsPath { get; set; } = string.Empty;
        public string CodeAnalysisPath { get; set; } = string.Empty;
        public string DependencyCheckPath { get; set; } = string.Empty;
        public string CycloneDxBomPath { get; set; } = string.Empty;
        public string LintReportPath { get; set; } = string.Empty;
        public Dictionary<string, string> Reports { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
