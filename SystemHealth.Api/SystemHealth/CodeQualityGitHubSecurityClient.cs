using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CRM.Application.SystemHealth;

internal sealed class CodeQualityGitHubSecurityClient
{
    private const string GitHubSecurityAdvisoryIdentifier = "GHSA";
    private const string DefaultCodeScanningSeverity = "WARNING";
    private const string SecretScanningDefaultPatternKey = "DEFAULT";
    private const string SecretScanningGenericPatternKey = "GENERIC";
    private const string GitHubLinkHeader = "Link";
    private const string GitHubNextLinkRelation = "rel=\"next\"";
    private const string DependabotAlertsDisabledMessage = "Dependabot alerts are disabled for this repository.";
    private const string CodeSecurityDisabledMessage = "Code Security must be enabled for this repository to use code scanning.";
    private const string CodeScanningDisabledMessage = "Code scanning is not enabled for this repository.";
    private const string SecretScanningDisabledMessage = "Secret scanning is disabled on this repository.";
    private static readonly string[] CodeScanningSeverityOrder = ["CRITICAL", "HIGH", "MEDIUM", "LOW", "ERROR", DefaultCodeScanningSeverity, "NOTE"];

    private readonly HttpClient _httpClient;

    public CodeQualityGitHubSecurityClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<GitHubSecurityData> GetSecurityAsync(
        CodeQualitySecurityOptions options,
        string organization,
        string repository,
        CancellationToken cancellationToken)
    {
        var alerts = new List<GitHubSecurityAlertDto>();
        var requestUri = CreateGitHubRestUri(
            options,
            organization,
            repository,
            "dependabot/alerts?state=open&per_page=100");

        while (requestUri is not null)
        {
            using var request = CreateGitHubRestRequest(options.GitHubToken, requestUri);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return await GetSecurityFromGraphQlAsync(options, organization, repository, cancellationToken);
            }

            if (await IsDependabotDisabledAsync(response, cancellationToken))
            {
                return GitHubSecurityData.Empty;
            }

            await EnsureGitHubRestSuccessAsync(response, "Dependabot alerts", cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (var alertElement in document.RootElement.EnumerateArray())
            {
                alerts.Add(CreateDependabotAlert(alertElement));
            }

            requestUri = GetNextPageUrl(response);
        }

        return new GitHubSecurityData(
            CreateDependabotSeverityCounts(alerts),
            alerts.ToArray(),
            GitHubCodeScanningData.Empty.Ref,
            GitHubCodeScanningData.Empty.HeadCommitSha,
            GitHubCodeScanningData.Empty.AnalysisCommitSha,
            GitHubCodeScanningData.Empty.AnalysisDateUtc,
            GitHubCodeScanningData.Empty.AnalysisKey,
            GitHubCodeScanningData.Empty.AnalysisCategory,
            GitHubCodeScanningData.Empty.IsCurrent,
            GitHubCodeScanningData.Empty.IsAnalysisPending,
            GitHubCodeScanningData.Empty.AnalysisRunUrl,
            GitHubCodeScanningData.Empty.SeverityCounts,
            GitHubCodeScanningData.Empty.Alerts,
            GitHubSecretScanningData.Empty.Counts,
            GitHubSecretScanningData.Empty.Alerts);
    }

    private static async Task<bool> IsDependabotDisabledAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.Forbidden)
        {
            return false;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        return IsDependabotAlertsDisabled(detail);
    }

    private static async Task EnsureGitHubRestSuccessAsync(
        HttpResponseMessage response,
        string source,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(BuildGitHubApiErrorMessage(source, response.StatusCode, detail));
    }

    private async Task<GitHubSecurityData> GetSecurityFromGraphQlAsync(
        CodeQualitySecurityOptions options,
        string organization,
        string repository,
        CancellationToken cancellationToken)
    {
        const string query = """
            query ($owner: String!, $name: String!) {
              repository(owner: $owner, name: $name) {
                vulnerabilityAlerts(first: 100, states: OPEN) {
                  nodes {
                    securityVulnerability {
                      severity
                      advisory {
                        summary
                        identifiers { type value }
                      }
                    }
                  }
                }
              }
            }
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, options.GitHubGraphQlUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.GitHubToken);
        request.Headers.UserAgent.ParseAdd("systemhealth-test12");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                query,
                variables = new { owner = organization, name = repository }
            }),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var alerts = new List<GitHubSecurityAlertDto>();
        var nodes = document.RootElement
            .GetProperty("data")
            .GetProperty("repository")
            .GetProperty("vulnerabilityAlerts")
            .GetProperty("nodes");

        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("securityVulnerability", out var vulnerabilityElement))
            {
                continue;
            }

            var severity = vulnerabilityElement.TryGetProperty("severity", out var severityElement)
                ? NormalizeDependabotSeverity(GetStringValue(severityElement))
                : string.Empty;

            var advisory = vulnerabilityElement.TryGetProperty("advisory", out var advisoryElement)
                ? advisoryElement
                : default;
            alerts.Add(new GitHubSecurityAlertDto
            {
                Severity = severity,
                Identifier = ResolveIdentifier(advisory),
                Summary = GetStringProperty(advisory, "summary")
            });
        }

        return new GitHubSecurityData(
            CreateDependabotSeverityCounts(alerts),
            alerts.ToArray(),
            GitHubCodeScanningData.Empty.Ref,
            GitHubCodeScanningData.Empty.HeadCommitSha,
            GitHubCodeScanningData.Empty.AnalysisCommitSha,
            GitHubCodeScanningData.Empty.AnalysisDateUtc,
            GitHubCodeScanningData.Empty.AnalysisKey,
            GitHubCodeScanningData.Empty.AnalysisCategory,
            GitHubCodeScanningData.Empty.IsCurrent,
            GitHubCodeScanningData.Empty.IsAnalysisPending,
            GitHubCodeScanningData.Empty.AnalysisRunUrl,
            GitHubCodeScanningData.Empty.SeverityCounts,
            GitHubCodeScanningData.Empty.Alerts,
            GitHubSecretScanningData.Empty.Counts,
            GitHubSecretScanningData.Empty.Alerts);
    }

    private static Uri CreateGitHubRestUri(
        CodeQualitySecurityOptions options,
        string organization,
        string repository,
        string relativePath)
    {
        return new Uri(
            $"{options.GitHubRestApiUrl.TrimEnd('/')}/repos/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(repository)}/{relativePath}",
            UriKind.Absolute);
    }

    private static Uri CreateGitHubRepositoryUri(
        CodeQualitySecurityOptions options,
        string organization,
        string repository)
    {
        return new Uri(
            $"{options.GitHubRestApiUrl.TrimEnd('/')}/repos/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(repository)}",
            UriKind.Absolute);
    }

    private static HttpRequestMessage CreateGitHubRestRequest(string gitHubToken, Uri requestUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gitHubToken);
        request.Headers.UserAgent.ParseAdd("systemhealth-test12");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return request;
    }

    public async Task<GitHubCodeScanningData> GetCodeScanningAsync(
        CodeQualitySecurityOptions options,
        string organization,
        string repository,
        string configuredRef,
        CancellationToken cancellationToken)
    {
        var resolvedRef = await ResolveCodeScanningRefAsync(options, organization, repository, configuredRef, cancellationToken);
        var freshness = await GetCodeScanningFreshnessAsync(options, organization, repository, resolvedRef, cancellationToken);
        var counts = GitHubCodeScanningData.Empty.SeverityCounts.ToDictionary(
            count => count.Severity,
            count => count.Count,
            StringComparer.OrdinalIgnoreCase);
        var alerts = new List<GitHubCodeScanningAlertDto>();
        var requestUri = CreateGitHubRestUri(
            options,
            organization,
            repository,
            CreateCodeScanningAlertsRelativePath(resolvedRef));

        while (requestUri is not null)
        {
            using var request = CreateGitHubRestRequest(options.GitHubToken, requestUri);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return GitHubCodeScanningData.Empty;
            }

            if (await IsCodeScanningDisabledAsync(response, cancellationToken))
            {
                return GitHubCodeScanningData.Empty;
            }

            await EnsureGitHubRestSuccessAsync(response, "CodeQL alerts", cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new GitHubCodeScanningData(
                    resolvedRef,
                    freshness.HeadCommitSha,
                    freshness.AnalysisCommitSha,
                    freshness.AnalysisDateUtc,
                    freshness.AnalysisKey,
                    freshness.AnalysisCategory,
                    freshness.IsCurrent,
                    freshness.IsAnalysisPending,
                    freshness.AnalysisRunUrl,
                    CreateCodeScanningSeverityCounts(counts),
                    alerts.ToArray());
            }

            foreach (var alertElement in document.RootElement.EnumerateArray())
            {
                var alert = CreateCodeScanningAlert(alertElement);
                if (!IsCurrentCodeScanningAlert(alert, freshness))
                {
                    continue;
                }

                if (counts.TryGetValue(alert.Severity, out var currentCount))
                {
                    counts[alert.Severity] = currentCount + 1;
                }

                alerts.Add(alert);
            }

            requestUri = GetNextPageUrl(response);
        }

        return new GitHubCodeScanningData(
            resolvedRef,
            freshness.HeadCommitSha,
            freshness.AnalysisCommitSha,
            freshness.AnalysisDateUtc,
            freshness.AnalysisKey,
            freshness.AnalysisCategory,
            freshness.IsCurrent,
            freshness.IsAnalysisPending,
            freshness.AnalysisRunUrl,
            CreateCodeScanningSeverityCounts(counts),
            alerts.ToArray());
    }

    private static bool IsCurrentCodeScanningAlert(
        GitHubCodeScanningAlertDto alert,
        GitHubCodeScanningFreshnessData freshness)
    {
        if (string.IsNullOrWhiteSpace(freshness.AnalysisCommitSha))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(alert.CommitSha)
            && IsSameCommit(alert.CommitSha, freshness.AnalysisCommitSha);
    }

    private static bool IsSameCommit(string left, string right)
    {
        return left.StartsWith(right, StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateCodeScanningAlertsRelativePath(string codeScanningRef)
    {
        return string.IsNullOrWhiteSpace(codeScanningRef)
            ? "code-scanning/alerts?state=open&per_page=100"
            : $"code-scanning/alerts?state=open&ref={Uri.EscapeDataString(codeScanningRef)}&per_page=100";
    }

    private async Task<GitHubCodeScanningFreshnessData> GetCodeScanningFreshnessAsync(
        CodeQualitySecurityOptions options,
        string organization,
        string repository,
        string codeScanningRef,
        CancellationToken cancellationToken)
    {
        var branchName = ResolveBranchName(codeScanningRef);
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return GitHubCodeScanningFreshnessData.Empty;
        }

        var headCommitSha = await GetBranchHeadCommitShaAsync(options, organization, repository, branchName, cancellationToken);
        var latestAnalysis = await GetLatestCodeScanningAnalysisAsync(options, organization, repository, codeScanningRef, cancellationToken);
        var isCurrent = latestAnalysis.IsUsable
            && !string.IsNullOrWhiteSpace(headCommitSha)
            && string.Equals(headCommitSha, latestAnalysis.CommitSha, StringComparison.OrdinalIgnoreCase);
        var pendingRun = isCurrent
            ? GitHubCodeQlWorkflowRunData.Empty
            : await GetPendingCodeQlWorkflowRunAsync(options, organization, repository, branchName, headCommitSha, cancellationToken);
        return new GitHubCodeScanningFreshnessData(
            headCommitSha,
            latestAnalysis.CommitSha,
            latestAnalysis.CreatedAtUtc,
            latestAnalysis.AnalysisKey,
            latestAnalysis.Category,
            isCurrent,
            pendingRun.IsPending,
            pendingRun.HtmlUrl);
    }

    private async Task<string> GetBranchHeadCommitShaAsync(
        CodeQualitySecurityOptions options,
        string organization,
        string repository,
        string branchName,
        CancellationToken cancellationToken)
    {
        using var request = CreateGitHubRestRequest(
            options.GitHubToken,
            CreateGitHubRestUri(options, organization, repository, $"branches/{Uri.EscapeDataString(branchName)}"));
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return string.Empty;
        }

        await EnsureGitHubRestSuccessAsync(response, "GitHub branch metadata", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var commit = GetObjectProperty(document.RootElement, "commit");
        return GetStringProperty(commit, "sha");
    }

    private async Task<GitHubCodeScanningAnalysisData> GetLatestCodeScanningAnalysisAsync(
        CodeQualitySecurityOptions options,
        string organization,
        string repository,
        string codeScanningRef,
        CancellationToken cancellationToken)
    {
        var relativePath = string.IsNullOrWhiteSpace(codeScanningRef)
            ? "code-scanning/analyses?tool_name=CodeQL&per_page=1"
            : $"code-scanning/analyses?ref={Uri.EscapeDataString(codeScanningRef)}&tool_name=CodeQL&per_page=1";
        using var request = CreateGitHubRestRequest(options.GitHubToken, CreateGitHubRestUri(options, organization, repository, relativePath));
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return GitHubCodeScanningAnalysisData.Empty;
        }

        if (await IsCodeScanningDisabledAsync(response, cancellationToken))
        {
            return GitHubCodeScanningAnalysisData.Empty;
        }

        await EnsureGitHubRestSuccessAsync(response, "CodeQL analyses", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return GitHubCodeScanningAnalysisData.Empty;
        }

        var latestAnalysis = document.RootElement.EnumerateArray().FirstOrDefault();
        if (latestAnalysis.ValueKind != JsonValueKind.Object)
        {
            return GitHubCodeScanningAnalysisData.Empty;
        }

        return new GitHubCodeScanningAnalysisData(
            GetStringProperty(latestAnalysis, "commit_sha"),
            GetNullableDateTimeProperty(latestAnalysis, "created_at"),
            GetStringProperty(latestAnalysis, "analysis_key"),
            GetStringProperty(latestAnalysis, "category"),
            GetStringProperty(latestAnalysis, "error"),
            GetNullableIntProperty(latestAnalysis, "rules_count"));
    }

    private async Task<GitHubCodeQlWorkflowRunData> GetPendingCodeQlWorkflowRunAsync(
        CodeQualitySecurityOptions options,
        string organization,
        string repository,
        string branchName,
        string headCommitSha,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(branchName) || string.IsNullOrWhiteSpace(headCommitSha))
        {
            return GitHubCodeQlWorkflowRunData.Empty;
        }

        try
        {
            var relativePath = $"actions/runs?branch={Uri.EscapeDataString(branchName)}&head_sha={Uri.EscapeDataString(headCommitSha)}&per_page=20";
            using var request = CreateGitHubRestRequest(
                options.GitHubToken,
                CreateGitHubRestUri(options, organization, repository, relativePath));
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return GitHubCodeQlWorkflowRunData.Empty;
            }

            await EnsureGitHubRestSuccessAsync(response, "GitHub Actions workflow runs", cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("workflow_runs", out var runsElement)
                || runsElement.ValueKind != JsonValueKind.Array)
            {
                return GitHubCodeQlWorkflowRunData.Empty;
            }

            foreach (var run in runsElement.EnumerateArray())
            {
                if (!IsCodeQlWorkflowRun(run) || !IsPendingWorkflowRun(run))
                {
                    continue;
                }

                return new GitHubCodeQlWorkflowRunData(true, NormalizeOptionalUrl(GetStringProperty(run, "html_url")));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            return GitHubCodeQlWorkflowRunData.Empty;
        }

        return GitHubCodeQlWorkflowRunData.Empty;
    }

    private static bool IsCodeQlWorkflowRun(JsonElement run)
    {
        var name = GetStringProperty(run, "name");
        var path = GetStringProperty(run, "path");
        return name.Contains("CodeQL", StringComparison.OrdinalIgnoreCase)
            || path.Contains("codeql", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPendingWorkflowRun(JsonElement run)
    {
        var status = GetStringProperty(run, "status");
        return status.Equals("queued", StringComparison.OrdinalIgnoreCase)
            || status.Equals("in_progress", StringComparison.OrdinalIgnoreCase)
            || status.Equals("requested", StringComparison.OrdinalIgnoreCase)
            || status.Equals("waiting", StringComparison.OrdinalIgnoreCase)
            || status.Equals("pending", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeOptionalUrl(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim();
    }

    private async Task<string> ResolveCodeScanningRefAsync(
        CodeQualitySecurityOptions options,
        string organization,
        string repository,
        string configuredRef,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configuredRef))
        {
            return NormalizeGitRef(configuredRef);
        }

        using var request = CreateGitHubRestRequest(options.GitHubToken, CreateGitHubRepositoryUri(options, organization, repository));
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return string.Empty;
        }

        await EnsureGitHubRestSuccessAsync(response, "GitHub repository metadata", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("default_branch", out var defaultBranchElement)
            ? NormalizeGitRef(GetStringValue(defaultBranchElement))
            : string.Empty;
    }

    private static string NormalizeGitRef(string? gitRef)
    {
        var trimmedRef = gitRef?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedRef))
        {
            return string.Empty;
        }

        return trimmedRef.StartsWith("refs/", StringComparison.Ordinal)
            ? trimmedRef
            : $"refs/heads/{trimmedRef}";
    }

    private static string ResolveBranchName(string codeScanningRef)
    {
        const string headsPrefix = "refs/heads/";
        if (string.IsNullOrWhiteSpace(codeScanningRef))
        {
            return string.Empty;
        }

        return codeScanningRef.StartsWith(headsPrefix, StringComparison.Ordinal)
            ? codeScanningRef[headsPrefix.Length..]
            : string.Empty;
    }

    public async Task<GitHubSecretScanningData> GetSecretScanningAsync(
        CodeQualitySecurityOptions options,
        string organization,
        string repository,
        CancellationToken cancellationToken)
    {
        var alerts = new List<GitHubSecretScanningAlertDto>();
        var requestUri = CreateGitHubRestUri(
            options,
            organization,
            repository,
            "secret-scanning/alerts?state=open&per_page=100");

        while (requestUri is not null)
        {
            using var request = CreateGitHubRestRequest(options.GitHubToken, requestUri);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new HttpRequestException("Secret scanning is unavailable for this repository. Verify the GitHub repository is configured correctly and secret scanning is enabled.");
            }

            if (await IsSecretScanningDisabledAsync(response, cancellationToken))
            {
                throw new HttpRequestException(SecretScanningDisabledMessage);
            }

            await EnsureGitHubRestSuccessAsync(response, "Secret scanning alerts", cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new GitHubSecretScanningData(CreateSecretScanningCounts(alerts), alerts.ToArray());
            }

            alerts.AddRange(document.RootElement.EnumerateArray().Select(CreateSecretScanningAlert));
            requestUri = GetNextPageUrl(response);
        }

        return new GitHubSecretScanningData(CreateSecretScanningCounts(alerts), alerts.ToArray());
    }

    private static Uri? GetNextPageUrl(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(GitHubLinkHeader, out var linkHeaders))
        {
            return null;
        }

        foreach (var linkHeader in linkHeaders)
        {
            foreach (var link in linkHeader.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = link.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2
                    || !parts.Skip(1).Any(part => string.Equals(part, GitHubNextLinkRelation, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var url = parts[0];
                if (url.Length > 2
                    && url[0] == '<'
                    && url[^1] == '>'
                    && Uri.TryCreate(url[1..^1], UriKind.Absolute, out var nextPageUrl))
                {
                    return nextPageUrl;
                }
            }
        }

        return null;
    }

    private static GitHubSecurityAlertDto CreateDependabotAlert(JsonElement alert)
    {
        var advisory = alert.TryGetProperty("security_advisory", out var advisoryElement)
            ? advisoryElement
            : default;
        var vulnerability = alert.TryGetProperty("security_vulnerability", out var vulnerabilityElement)
            ? vulnerabilityElement
            : default;

        return new GitHubSecurityAlertDto
        {
            Severity = vulnerability.ValueKind == JsonValueKind.Object && vulnerability.TryGetProperty("severity", out var severityElement)
                ? NormalizeDependabotSeverity(GetStringValue(severityElement))
                : string.Empty,
            Identifier = ResolveIdentifier(advisory),
            Summary = GetStringProperty(advisory, "summary")
        };
    }

    private static string NormalizeDependabotSeverity(string? severity)
    {
        var normalizedSeverity = (severity ?? string.Empty).ToUpperInvariant();
        return string.Equals(normalizedSeverity, "MEDIUM", StringComparison.OrdinalIgnoreCase)
            ? "MODERATE"
            : normalizedSeverity;
    }

    private static GitHubSeverityCountDto[] CreateDependabotSeverityCounts(IEnumerable<GitHubSecurityAlertDto> alerts)
    {
        var counts = GitHubSecurityData.Empty.SeverityCounts.ToDictionary(
            count => count.Severity,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);

        foreach (var alert in alerts)
        {
            var severity = NormalizeDependabotSeverity(alert.Severity);
            if (counts.TryGetValue(severity, out var currentCount))
            {
                counts[severity] = currentCount + 1;
            }
        }

        return GitHubSecurityData.Empty.SeverityCounts
            .Select(count => new GitHubSeverityCountDto { Severity = count.Severity, Count = counts[count.Severity] })
            .ToArray();
    }

    private static string ResolveIdentifier(JsonElement advisory)
    {
        if (advisory.ValueKind != JsonValueKind.Object
            || !advisory.TryGetProperty("identifiers", out var identifiersElement)
            || identifiersElement.ValueKind != JsonValueKind.Array)
        {
            return GitHubSecurityAdvisoryIdentifier;
        }

        var ghsa = GitHubSecurityAdvisoryIdentifier;
        foreach (var identifier in identifiersElement.EnumerateArray())
        {
            var type = identifier.TryGetProperty("type", out var typeElement) ? GetStringValue(typeElement) : string.Empty;
            var value = identifier.TryGetProperty("value", out var valueElement) ? GetStringValue(valueElement) : string.Empty;
            if (string.Equals(type, "CVE", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            if (string.Equals(type, GitHubSecurityAdvisoryIdentifier, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                ghsa = value;
            }
        }

        return ghsa;
    }

    private static string BuildGitHubApiErrorMessage(string source, System.Net.HttpStatusCode statusCode, string detail)
    {
        return string.IsNullOrWhiteSpace(detail)
            ? $"{source} returned {(int)statusCode} {statusCode}."
            : $"{source} returned {(int)statusCode} {statusCode}. {ExtractGitHubErrorMessage(detail)}";
    }

    private static string ExtractGitHubErrorMessage(string detail)
    {
        try
        {
            using var document = JsonDocument.Parse(detail);
            return document.RootElement.TryGetProperty("message", out var messageElement)
                ? GetStringValue(messageElement, detail)
                : detail;
        }
        catch (JsonException)
        {
            return detail;
        }
    }

    private static bool IsDependabotAlertsDisabled(string detail)
    {
        return string.Equals(ExtractGitHubErrorMessage(detail), DependabotAlertsDisabledMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsCodeScanningDisabledAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.Forbidden)
        {
            return false;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = ExtractGitHubErrorMessage(detail);
        return message.Contains(CodeSecurityDisabledMessage, StringComparison.OrdinalIgnoreCase)
            || message.Contains(CodeScanningDisabledMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsSecretScanningDisabledAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.Forbidden)
        {
            return false;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        return ExtractGitHubErrorMessage(detail).Contains(SecretScanningDisabledMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static GitHubCodeScanningAlertDto CreateCodeScanningAlert(JsonElement alert)
    {
        var rule = GetObjectProperty(alert, "rule");
        var instance = GetObjectProperty(alert, "most_recent_instance");
        var location = GetObjectProperty(instance, "location");
        var message = GetObjectProperty(instance, "message");

        return new GitHubCodeScanningAlertDto
        {
            Severity = ResolveCodeScanningSeverity(rule),
            RuleId = GetStringProperty(rule, "id"),
            Message = ResolveCodeScanningMessage(rule, message),
            Path = GetStringProperty(location, "path"),
            Line = GetNullableIntProperty(location, "start_line"),
            Ref = GetStringProperty(instance, "ref"),
            CommitSha = GetStringProperty(instance, "commit_sha"),
            FixedAtUtc = GetNullableDateTimeProperty(alert, "fixed_at"),
            ToolName = GetCodeScanningToolName(instance),
            Url = GetStringProperty(alert, "html_url")
        };
    }

    private static string GetCodeScanningToolName(JsonElement instance)
    {
        var analysis = GetObjectProperty(instance, "analysis");
        return GetStringProperty(analysis, "tool_name");
    }

    private static JsonElement GetObjectProperty(JsonElement parent, string propertyName)
    {
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Object
                ? property
                : default;
    }

    private static string GetStringProperty(JsonElement parent, string propertyName)
    {
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(propertyName, out var property)
                ? GetStringValue(property)
                : string.Empty;
    }

    private static string GetStringValue(JsonElement element, string defaultValue = "")
    {
        return element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? defaultValue
            : defaultValue;
    }

    private static int? GetNullableIntProperty(JsonElement parent, string propertyName)
    {
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
                ? value
                : null;
    }

    private static DateTime? GetNullableDateTimeProperty(JsonElement parent, string propertyName)
    {
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.TryGetDateTime(out var value)
                ? value
                : null;
    }

    private static GitHubSecretScanningAlertDto CreateSecretScanningAlert(JsonElement alert)
    {
        var secretType = alert.TryGetProperty("secret_type", out var secretTypeElement)
            ? GetStringValue(secretTypeElement)
            : string.Empty;
        var displayName = alert.TryGetProperty("secret_type_display_name", out var displayNameElement)
            ? GetStringValue(displayNameElement)
            : string.Empty;
        var createdAtUtc = alert.TryGetProperty("created_at", out var createdAtElement)
            && createdAtElement.ValueKind == JsonValueKind.String
            && createdAtElement.TryGetDateTime(out var parsedCreatedAt)
                ? parsedCreatedAt
                : (DateTime?)null;

        return new GitHubSecretScanningAlertDto
        {
            SecretType = secretType,
            SecretTypeDisplayName = displayName,
            Pattern = ResolveSecretScanningPattern(secretType, displayName),
            State = alert.TryGetProperty("state", out var stateElement)
                ? GetStringValue(stateElement)
                : string.Empty,
            CreatedAtUtc = createdAtUtc,
            Url = alert.TryGetProperty("html_url", out var urlElement)
                ? GetStringValue(urlElement)
                : string.Empty
        };
    }

    private static string ResolveSecretScanningPattern(string secretType, string displayName)
    {
        return secretType.Contains("generic", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("generic", StringComparison.OrdinalIgnoreCase)
                ? SecretScanningGenericPatternKey
                : SecretScanningDefaultPatternKey;
    }

    private static string ResolveCodeScanningSeverity(JsonElement rule)
    {
        if (rule.ValueKind != JsonValueKind.Object)
        {
            return DefaultCodeScanningSeverity;
        }

        if (rule.TryGetProperty("security_severity_level", out var securitySeverityElement)
            && !string.IsNullOrWhiteSpace(GetStringValue(securitySeverityElement)))
        {
            return GetStringValue(securitySeverityElement).ToUpperInvariant();
        }

        return rule.TryGetProperty("severity", out var severityElement)
            ? GetStringValue(severityElement, DefaultCodeScanningSeverity).ToUpperInvariant()
            : DefaultCodeScanningSeverity;
    }

    private static string ResolveCodeScanningMessage(JsonElement rule, JsonElement message)
    {
        if (message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("text", out var messageTextElement)
            && !string.IsNullOrWhiteSpace(GetStringValue(messageTextElement)))
        {
            return GetStringValue(messageTextElement);
        }

        if (rule.ValueKind == JsonValueKind.Object
            && rule.TryGetProperty("description", out var descriptionElement)
            && !string.IsNullOrWhiteSpace(GetStringValue(descriptionElement)))
        {
            return GetStringValue(descriptionElement);
        }

        return rule.ValueKind == JsonValueKind.Object && rule.TryGetProperty("name", out var nameElement)
            ? GetStringValue(nameElement)
            : string.Empty;
    }

    private static GitHubSeverityCountDto[] CreateCodeScanningSeverityCounts(Dictionary<string, int> counts)
    {
        return CodeScanningSeverityOrder
            .Select(severity => new GitHubSeverityCountDto { Severity = severity, Count = counts.TryGetValue(severity, out var count) ? count : 0 })
            .ToArray();
    }

    private static GitHubSecretScanningCountDto[] CreateSecretScanningCounts(List<GitHubSecretScanningAlertDto> alerts)
    {
        return
        [
            new GitHubSecretScanningCountDto { Key = "OPEN", Label = "Open Secrets", Count = alerts.Count },
            new GitHubSecretScanningCountDto
            {
                Key = SecretScanningDefaultPatternKey,
                Label = "Default Pattern Alerts",
                Count = alerts.Count(alert => string.Equals(alert.Pattern, SecretScanningDefaultPatternKey, StringComparison.OrdinalIgnoreCase))
            },
            new GitHubSecretScanningCountDto
            {
                Key = SecretScanningGenericPatternKey,
                Label = "Generic Pattern Alerts",
                Count = alerts.Count(alert => string.Equals(alert.Pattern, SecretScanningGenericPatternKey, StringComparison.OrdinalIgnoreCase))
            }
        ];
    }
}

internal sealed record GitHubSecurityData(
    IReadOnlyList<GitHubSeverityCountDto> SeverityCounts,
    IReadOnlyList<GitHubSecurityAlertDto> Alerts,
    string CodeScanningRef,
    string CodeScanningHeadCommitSha,
    string CodeScanningAnalysisCommitSha,
    DateTime? CodeScanningAnalysisDateUtc,
    string CodeScanningAnalysisKey,
    string CodeScanningAnalysisCategory,
    bool CodeScanningIsCurrent,
    bool CodeScanningIsAnalysisPending,
    string CodeScanningAnalysisRunUrl,
    IReadOnlyList<GitHubSeverityCountDto> CodeScanningSeverityCounts,
    IReadOnlyList<GitHubCodeScanningAlertDto> CodeScanningAlerts,
    IReadOnlyList<GitHubSecretScanningCountDto> SecretScanningCounts,
    IReadOnlyList<GitHubSecretScanningAlertDto> SecretScanningAlerts)
{
    public static GitHubSecurityData Empty { get; } = new(
        [
            new GitHubSeverityCountDto { Severity = "CRITICAL", Count = 0 },
            new GitHubSeverityCountDto { Severity = "HIGH", Count = 0 },
            new GitHubSeverityCountDto { Severity = "MODERATE", Count = 0 },
            new GitHubSeverityCountDto { Severity = "LOW", Count = 0 }
        ],
        Array.Empty<GitHubSecurityAlertDto>(),
        string.Empty,
        string.Empty,
        string.Empty,
        null,
        string.Empty,
        string.Empty,
        false,
        false,
        string.Empty,
        GitHubCodeScanningData.Empty.SeverityCounts,
        GitHubCodeScanningData.Empty.Alerts,
        GitHubSecretScanningData.Empty.Counts,
        GitHubSecretScanningData.Empty.Alerts);
}

internal sealed record GitHubCodeScanningData(
    string Ref,
    string HeadCommitSha,
    string AnalysisCommitSha,
    DateTime? AnalysisDateUtc,
    string AnalysisKey,
    string AnalysisCategory,
    bool IsCurrent,
    bool IsAnalysisPending,
    string AnalysisRunUrl,
    IReadOnlyList<GitHubSeverityCountDto> SeverityCounts,
    IReadOnlyList<GitHubCodeScanningAlertDto> Alerts)
{
    public static GitHubCodeScanningData Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        null,
        string.Empty,
        string.Empty,
        false,
        false,
        string.Empty,
        [
            new GitHubSeverityCountDto { Severity = "CRITICAL", Count = 0 },
            new GitHubSeverityCountDto { Severity = "HIGH", Count = 0 },
            new GitHubSeverityCountDto { Severity = "MEDIUM", Count = 0 },
            new GitHubSeverityCountDto { Severity = "LOW", Count = 0 },
            new GitHubSeverityCountDto { Severity = "ERROR", Count = 0 },
            new GitHubSeverityCountDto { Severity = "WARNING", Count = 0 },
            new GitHubSeverityCountDto { Severity = "NOTE", Count = 0 }
        ],
        Array.Empty<GitHubCodeScanningAlertDto>());
}

internal sealed record GitHubCodeScanningFreshnessData(
    string HeadCommitSha,
    string AnalysisCommitSha,
    DateTime? AnalysisDateUtc,
    string AnalysisKey,
    string AnalysisCategory,
    bool IsCurrent,
    bool IsAnalysisPending,
    string AnalysisRunUrl)
{
    public static GitHubCodeScanningFreshnessData Empty { get; } = new(
        string.Empty,
        string.Empty,
        null,
        string.Empty,
        string.Empty,
        false,
        false,
        string.Empty);
}

internal sealed record GitHubCodeQlWorkflowRunData(
    bool IsPending,
    string HtmlUrl)
{
    public static GitHubCodeQlWorkflowRunData Empty { get; } = new(false, string.Empty);
}

internal sealed record GitHubCodeScanningAnalysisData(
    string CommitSha,
    DateTime? CreatedAtUtc,
    string AnalysisKey,
    string Category,
    string Error,
    int? RulesCount)
{
    public bool IsUsable =>
        string.IsNullOrWhiteSpace(Error)
        && (!RulesCount.HasValue || RulesCount.Value > 0);

    public static GitHubCodeScanningAnalysisData Empty { get; } = new(
        string.Empty,
        null,
        string.Empty,
        string.Empty,
        string.Empty,
        null);
}

internal sealed record GitHubSecretScanningData(
    IReadOnlyList<GitHubSecretScanningCountDto> Counts,
    IReadOnlyList<GitHubSecretScanningAlertDto> Alerts)
{
    public static GitHubSecretScanningData Empty { get; } = new(
        [
            new GitHubSecretScanningCountDto { Key = "OPEN", Label = "Open Secrets", Count = 0 },
            new GitHubSecretScanningCountDto { Key = "DEFAULT", Label = "Default Pattern Alerts", Count = 0 },
            new GitHubSecretScanningCountDto { Key = "GENERIC", Label = "Generic Pattern Alerts", Count = 0 }
        ],
        Array.Empty<GitHubSecretScanningAlertDto>());
}
