using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

sealed class CodeQualitySecurityService
{
    private const string ApplicationKey = "my-life-story-vault";
    private const string ApplicationLabel = "My Life Story Vault";
    private const string EnvironmentName = "Development";
    private const int DetailLimit = 100;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly SystemHealthOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public CodeQualitySecurityService(SystemHealthOptions options, IHttpClientFactory httpClientFactory)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<IResult> GetAsync(CancellationToken cancellationToken)
    {
        var sonar = await ReadSonarAsync(cancellationToken);
        var dependabot = await ReadGitHubAlertsAsync("GitHub Dependabot", "dependabot/alerts?state=open&per_page=100", "security_advisory", cancellationToken);
        var codeQl = await ReadGitHubAlertsAsync("GitHub CodeQL", "code-scanning/alerts?state=open&per_page=100", "rule", cancellationToken);
        var secretScanning = await ReadGitHubAlertsAsync("GitHub Secret Scanning", "secret-scanning/alerts?state=open&per_page=100", "secret_type", cancellationToken);
        var lint = ReadLintReport();
        var dependencyCheck = ReadDependencyCheckReport();
        var cycloneDx = ReadCycloneDxBom();
        var playwright = ReadPlaywrightReport();

        var providerStatuses = new[]
        {
            sonar.ProviderStatus,
            dependabot.ProviderStatus,
            codeQl.ProviderStatus,
            secretScanning.ProviderStatus,
            ProviderStatus("Lint & Standards", lint.Status, lint.Detail),
            ProviderStatus("OWASP Dependency-Check", dependencyCheck.Status, dependencyCheck.Detail),
            ProviderStatus("CycloneDX SBOM", cycloneDx.Status, cycloneDx.Detail),
            ProviderStatus("Playwright", playwright.Status, playwright.Detail)
        };

        var status = providerStatuses.All(item => item.Status == "Success" || item.Status == "Healthy")
            && NoFindings(sonar, dependabot, codeQl, secretScanning, lint, dependencyCheck, playwright)
                ? "Healthy"
                : "Warning";

        return Results.Json(new
        {
            status,
            statusDetail = "Code Quality & Security is wired to MyLifeStoryVault-Ltd/My-Life-Story-Vault live providers and configured Jenkins artifacts. Missing runtime credentials or artifacts are reported as unavailable instead of using placeholders.",
            generatedAtUtc = DateTimeOffset.UtcNow,
            selectedApplicationKey = ApplicationKey,
            selectedEnvironment = EnvironmentName,
            applications = new[] { new { key = ApplicationKey, label = ApplicationLabel } },
            environments = new[] { EnvironmentName },
            providerStatuses,
            sonarProjectKey = _options.SonarQube.ProjectKey,
            sonarAnalysisDateUtc = sonar.AnalysisDateUtc,
            sonarAnalysisRevision = sonar.AnalysisRevision,
            sonarAnalysisVersion = sonar.AnalysisVersion,
            sonarMetrics = sonar.Metrics,
            vulnerabilities = sonar.Vulnerabilities,
            vulnerabilityDisplayedCount = sonar.Vulnerabilities.Length,
            vulnerabilityTotalCount = sonar.VulnerabilityTotalCount,
            bugs = sonar.Bugs,
            bugDisplayedCount = sonar.Bugs.Length,
            bugTotalCount = sonar.BugTotalCount,
            codeSmells = sonar.CodeSmells,
            codeSmellDisplayedCount = sonar.CodeSmells.Length,
            codeSmellTotalCount = sonar.CodeSmellTotalCount,
            gitHubRepository = $"{_options.Repository.Owner}/{_options.Repository.Name}",
            gitHubSeverityCounts = dependabot.SeverityCounts,
            gitHubAlerts = dependabot.Alerts,
            gitHubCodeScanningSeverityCounts = codeQl.SeverityCounts,
            gitHubCodeScanningAlerts = codeQl.Alerts,
            gitHubSecretScanningCounts = secretScanning.SecretCounts,
            gitHubSecretScanningAlerts = secretScanning.Alerts,
            lintStatus = lint.Status,
            lintStatusDetail = lint.Detail,
            lintGeneratedAtUtc = lint.GeneratedAtUtc,
            lintErrorCount = lint.ErrorCount,
            lintWarningCount = lint.WarningCount,
            lintToolsTotal = lint.ToolsTotal,
            lintToolsPassed = lint.ToolsPassed,
            lintToolsFailed = lint.ToolsFailed,
            lintToolsNotApplicable = lint.ToolsNotApplicable,
            lintToolsNotConfigured = lint.ToolsNotConfigured,
            lintFindings = lint.Findings,
            lintDisplayedCount = lint.Findings.Length,
            lintTotalFindings = lint.TotalFindings,
            dependencyCheckStatus = dependencyCheck.Status,
            dependencyCheckStatusDetail = dependencyCheck.Detail,
            dependencyCheckSeverityCounts = dependencyCheck.SeverityCounts,
            dependencyCheckVulnerabilityCount = dependencyCheck.TotalFindings,
            dependencyCheckFindings = dependencyCheck.Findings,
            cycloneDxStatus = cycloneDx.Status,
            cycloneDxStatusDetail = cycloneDx.Detail,
            cycloneDxComponents = cycloneDx.Components,
            cycloneDxComponentCount = cycloneDx.ComponentCount,
            playwrightStatus = playwright.Status,
            playwrightStatusDetail = playwright.Detail,
            playwrightTotalTests = playwright.TotalTests,
            playwrightPassedTests = playwright.PassedTests,
            playwrightFailedTests = playwright.FailedTests,
            playwrightSkippedTests = playwright.SkippedTests,
            playwrightResults = playwright.Results,
            playwrightWorkflowContracts = playwright.WorkflowContracts
        }, JsonOptions);
    }

    private async Task<SonarSnapshot> ReadSonarAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured(_options.SonarQube.BaseUrl) || !IsConfigured(_options.SonarQube.ProjectKey))
        {
            return SonarSnapshot.Unavailable(_options.SonarQube.ProjectKey, "SonarQube base URL or project key is not configured for Test12 runtime.");
        }

        try
        {
            var client = _httpClientFactory.CreateClient("sonarqube");
            ApplySonarToken(client, _options.SonarQube.Token);
            var baseUrl = _options.SonarQube.BaseUrl.TrimEnd('/');
            var component = Uri.EscapeDataString(_options.SonarQube.ProjectKey);
            var metricKeys = "vulnerabilities,coverage,reliability_rating,bugs,duplicated_lines_density,code_smells,security_hotspots,security_rating,sqale_rating";
            var measures = await ReadJsonAsync(client, $"{baseUrl}/api/measures/component?component={component}&metricKeys={metricKeys}", cancellationToken);
            var issues = await ReadJsonAsync(client, $"{baseUrl}/api/issues/search?componentKeys={component}&types=VULNERABILITY,BUG,CODE_SMELL&resolved=false&ps={DetailLimit}", cancellationToken);
            var analyses = await TryReadJsonAsync(client, $"{baseUrl}/api/project_analyses/search?project={component}&ps=1", cancellationToken);

            var metrics = measures?["component"]?["measures"]?.AsArray()
                .Select(item => new
                {
                    key = Text(item?["metric"]),
                    metric = Text(item?["metric"]),
                    label = Text(item?["metric"]),
                    value = Text(item?["value"]),
                    bestValue = item?["bestValue"]?.GetValue<bool?>()
                })
                .Where(item => IsConfigured(item.key))
                .Cast<object>()
                .ToArray() ?? Array.Empty<object>();

            var rows = issues?["issues"]?.AsArray()
                .Select(MapSonarIssue)
                .Where(item => item is not null)
                .Cast<object>()
                .ToArray() ?? Array.Empty<object>();

            return new SonarSnapshot(
                ProviderStatus("SonarQube", "Success", $"Read SonarQube project {_options.SonarQube.ProjectKey}."),
                metrics,
                rows.Where(item => HasType(item, "VULNERABILITY")).ToArray(),
                rows.Count(item => HasType(item, "VULNERABILITY")),
                rows.Where(item => HasType(item, "BUG")).ToArray(),
                rows.Count(item => HasType(item, "BUG")),
                rows.Where(item => HasType(item, "CODE_SMELL")).ToArray(),
                rows.Count(item => HasType(item, "CODE_SMELL")),
                Text(analyses?["analyses"]?.AsArray().FirstOrDefault()?["date"]),
                Text(analyses?["analyses"]?.AsArray().FirstOrDefault()?["revision"]),
                Text(analyses?["analyses"]?.AsArray().FirstOrDefault()?["projectVersion"]));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return SonarSnapshot.Unavailable(_options.SonarQube.ProjectKey, $"SonarQube could not be queried for {_options.SonarQube.ProjectKey}: {ex.Message}");
        }
    }

    private async Task<GitHubSnapshot> ReadGitHubAlertsAsync(string provider, string endpoint, string categoryKey, CancellationToken cancellationToken)
    {
        if (!IsConfigured(_options.Repository.GitHubToken))
        {
            return GitHubSnapshot.Unavailable(provider, "GitHub token is not configured for Test12 runtime.");
        }

        try
        {
            var client = _httpClientFactory.CreateClient("github");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.Repository.GitHubToken);
            var apiBase = (IsConfigured(_options.Repository.GitHubApiBaseUrl) ? _options.Repository.GitHubApiBaseUrl : "https://api.github.com").TrimEnd('/');
            var url = $"{apiBase}/repos/{Uri.EscapeDataString(_options.Repository.Owner)}/{Uri.EscapeDataString(_options.Repository.Name)}/{endpoint}";
            var root = await ReadJsonAsync(client, url, cancellationToken);
            var rows = root?.AsArray()
                .Select(item => MapGitHubAlert(item, provider, categoryKey))
                .Where(item => item is not null)
                .Cast<object>()
                .ToArray() ?? Array.Empty<object>();

            return new GitHubSnapshot(
                ProviderStatus(provider, "Success", $"Read open {provider} alerts from {_options.Repository.Owner}/{_options.Repository.Name}."),
                SeverityCounts(rows),
                SecretCounts(rows),
                rows);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return GitHubSnapshot.Unavailable(provider, $"{provider} could not be queried for {_options.Repository.Owner}/{_options.Repository.Name}: {ex.Message}");
        }
    }

    private LintSnapshot ReadLintReport()
    {
        if (!TryReadArtifact(_options.Artifacts.LintReportPath, "Lint & Standards", out var root, out var status, out var detail))
        {
            return LintSnapshot.Empty(status, detail);
        }

        var findings = ArrayProperty(root, "findings")
            .Concat(ArrayProperty(root, "results"))
            .Select(MapLintFinding)
            .Where(item => item is not null)
            .Cast<object>()
            .Take(DetailLimit)
            .ToArray();

        var tools = ArrayProperty(root, "tools")
            .Concat(ArrayProperty(root, "checks"))
            .ToArray();

        var errorCount = IntProperty(root, "errorCount", "errors") ?? findings.Count(item => SeverityOf(item) is "ERROR" or "CRITICAL" or "HIGH");
        var warningCount = IntProperty(root, "warningCount", "warnings") ?? findings.Count(item => SeverityOf(item) is "WARNING" or "MEDIUM" or "LOW");
        return new LintSnapshot(
            errorCount > 0 ? "Warning" : "Healthy",
            errorCount > 0 || warningCount > 0 ? $"Lint artifact parsed with {errorCount} error(s) and {warningCount} warning(s)." : detail,
            Text(root?["generatedAtUtc"]) ?? Text(root?["generatedAt"]),
            errorCount,
            warningCount,
            IntProperty(root, "toolsTotal") ?? tools.Length,
            IntProperty(root, "toolsPassed") ?? tools.Count(IsHealthyTool),
            IntProperty(root, "toolsFailed") ?? tools.Count(IsFailedTool),
            IntProperty(root, "toolsNotApplicable") ?? tools.Count(item => StatusOf(item) == "NOT_APPLICABLE"),
            IntProperty(root, "toolsNotConfigured") ?? tools.Count(item => StatusOf(item) == "NOT_CONFIGURED"),
            findings,
            IntProperty(root, "totalFindings") ?? findings.Length);
    }

    private DependencyCheckSnapshot ReadDependencyCheckReport()
    {
        if (!TryReadArtifact(_options.Artifacts.DependencyCheckReportPath, "OWASP Dependency-Check", out var root, out var status, out var detail))
        {
            return DependencyCheckSnapshot.Empty(status, detail);
        }

        var findings = new List<object>();
        foreach (var dependency in ArrayProperty(root, "dependencies"))
        {
            foreach (var vulnerability in ArrayProperty(dependency, "vulnerabilities"))
            {
                findings.Add(new
                {
                    identifier = Text(vulnerability?["name"]) ?? Text(vulnerability?["source"]) ?? "Dependency vulnerability",
                    packageName = Text(dependency?["fileName"]) ?? Text(dependency?["packagePath"]),
                    file = Text(dependency?["filePath"]) ?? Text(dependency?["packagePath"]),
                    severity = NormalizeSeverity(Text(vulnerability?["severity"])),
                    cvssScore = Text(vulnerability?["cvssv3"]?["baseScore"]) ?? Text(vulnerability?["cvssv2"]?["score"]),
                    message = Text(vulnerability?["description"]) ?? Text(vulnerability?["name"]),
                    source = Text(vulnerability?["source"])
                });
            }
        }

        var displayed = findings.Take(DetailLimit).ToArray();
        return new DependencyCheckSnapshot(
            findings.Count > 0 ? "Warning" : "Healthy",
            findings.Count > 0 ? $"OWASP Dependency-Check artifact parsed with {findings.Count} vulnerability finding(s)." : detail,
            SeverityCounts(findings),
            findings.Count,
            displayed);
    }

    private CycloneDxSnapshot ReadCycloneDxBom()
    {
        if (!TryReadArtifact(_options.Artifacts.CycloneDxBomPath, "CycloneDX SBOM", out var root, out var status, out var detail))
        {
            return new CycloneDxSnapshot(status, detail, Array.Empty<object>(), 0);
        }

        var components = ArrayProperty(root, "components")
            .Select(item => new
            {
                name = Text(item?["name"]),
                version = Text(item?["version"]),
                type = Text(item?["type"]),
                bomRef = Text(item?["bom-ref"]),
                group = Text(item?["group"])
            })
            .Cast<object>()
            .Take(DetailLimit)
            .ToArray();

        return new CycloneDxSnapshot("Healthy", $"{detail} Parsed {ArrayProperty(root, "components").Count()} component(s).", components, ArrayProperty(root, "components").Count());
    }

    private PlaywrightSnapshot ReadPlaywrightReport()
    {
        if (!TryReadArtifact(_options.Artifacts.PlaywrightReportPath, "Playwright", out var root, out var status, out var detail))
        {
            return PlaywrightSnapshot.Empty(status, detail);
        }

        var results = FlattenPlaywrightResults(root).Take(DetailLimit).ToArray();
        var total = IntProperty(root, "totalTests", "total") ?? results.Length;
        var failed = IntProperty(root, "failedTests", "failed") ?? results.Count(item => StatusOf(item) == "FAILED");
        var skipped = IntProperty(root, "skippedTests", "skipped") ?? results.Count(item => StatusOf(item) == "SKIPPED");
        var passed = IntProperty(root, "passedTests", "passed") ?? Math.Max(0, total - failed - skipped);

        return new PlaywrightSnapshot(
            failed > 0 ? "Warning" : "Healthy",
            failed > 0 ? $"Playwright artifact parsed with {failed} failed workflow check(s)." : detail,
            total,
            passed,
            failed,
            skipped,
            results,
            Array.Empty<object>());
    }

    private bool TryReadArtifact(string? path, string name, out JsonNode? root, out string status, out string detail)
    {
        root = null;
        if (!IsConfigured(path))
        {
            status = "Unavailable";
            detail = $"{name} artifact path is not configured.";
            return false;
        }

        if (!File.Exists(path))
        {
            status = "Unavailable";
            detail = $"{name} artifact was not found at the configured runtime path.";
            return false;
        }

        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
            status = "Healthy";
            detail = $"{name} artifact exists at the configured runtime path.";
            return true;
        }
        catch (JsonException ex)
        {
            status = "Warning";
            detail = $"{name} artifact exists but could not be parsed as JSON: {ex.Message}";
            return false;
        }
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task<JsonNode?> TryReadJsonAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadJsonAsync(client, url, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static void ApplySonarToken(HttpClient client, string token)
    {
        if (!IsConfigured(token))
        {
            return;
        }

        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{token}:"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    }

    private static object? MapSonarIssue(JsonNode? item)
    {
        if (item is null)
        {
            return null;
        }

        return new
        {
            id = Text(item["key"]),
            key = Text(item["key"]),
            severity = NormalizeSeverity(Text(item["severity"])),
            type = Text(item["type"]),
            rule = Text(item["rule"]),
            component = Text(item["component"]),
            project = Text(item["project"]),
            line = Text(item["line"]),
            status = Text(item["status"]),
            effort = Text(item["effort"]),
            message = Text(item["message"]),
            creationDate = Text(item["creationDate"]),
            updateDate = Text(item["updateDate"])
        };
    }

    private static object? MapGitHubAlert(JsonNode? item, string provider, string categoryKey)
    {
        if (item is null)
        {
            return null;
        }

        var severity = provider switch
        {
            "GitHub Dependabot" => Text(item["security_vulnerability"]?["severity"]) ?? Text(item["security_advisory"]?["severity"]),
            "GitHub CodeQL" => Text(item["rule"]?["security_severity_level"]) ?? Text(item["rule"]?["severity"]),
            _ => "OPEN"
        };

        return new
        {
            id = Text(item["number"]) ?? Text(item["id"]),
            state = Text(item["state"]),
            severity = NormalizeSeverity(severity),
            ruleId = Text(item["rule"]?["id"]) ?? Text(item["security_advisory"]?["ghsa_id"]) ?? Text(item["secret_type"]),
            packageName = Text(item["dependency"]?["package"]?["name"]),
            ecosystem = Text(item["dependency"]?["package"]?["ecosystem"]),
            file = Text(item["most_recent_instance"]?["location"]?["path"]),
            line = Text(item["most_recent_instance"]?["location"]?["start_line"]),
            message = Text(item["security_advisory"]?["summary"]) ?? Text(item["rule"]?["description"]) ?? Text(item["secret_type_display_name"]),
            category = Text(item[categoryKey]),
            htmlUrl = Text(item["html_url"]),
            createdAt = Text(item["created_at"]),
            updatedAt = Text(item["updated_at"])
        };
    }

    private static object? MapLintFinding(JsonNode? item)
    {
        if (item is null)
        {
            return null;
        }

        return new
        {
            tool = Text(item["tool"]) ?? Text(item["category"]),
            severity = NormalizeSeverity(Text(item["severity"]) ?? Text(item["level"])),
            ruleId = Text(item["ruleId"]) ?? Text(item["rule"]) ?? Text(item["code"]),
            file = Text(item["file"]) ?? Text(item["path"]),
            line = Text(item["line"]),
            message = Text(item["message"]) ?? Text(item["detail"]) ?? Text(item["rawSummary"]),
            rawSummary = Text(item["rawSummary"])
        };
    }

    private static IEnumerable<object> FlattenPlaywrightResults(JsonNode? root)
    {
        foreach (var item in ArrayProperty(root, "results").Concat(ArrayProperty(root, "tests")))
        {
            yield return new
            {
                name = Text(item?["name"]) ?? Text(item?["title"]),
                status = Text(item?["status"]),
                duration = Text(item?["duration"]),
                file = Text(item?["file"]) ?? Text(item?["location"]?["file"]),
                line = Text(item?["line"]) ?? Text(item?["location"]?["line"]),
                message = Text(item?["message"]) ?? Text(item?["error"]?["message"])
            };
        }

        foreach (var suite in ArrayProperty(root, "suites"))
        {
            foreach (var spec in ArrayProperty(suite, "specs"))
            {
                foreach (var test in ArrayProperty(spec, "tests"))
                {
                    yield return new
                    {
                        name = Text(spec?["title"]) ?? Text(test?["title"]),
                        status = Text(test?["status"]) ?? Text(test?["outcome"]),
                        duration = Text(test?["duration"]),
                        file = Text(suite?["file"]),
                        line = Text(spec?["line"]),
                        message = Text(test?["error"]?["message"])
                    };
                }
            }
        }
    }

    private static JsonArray ArrayProperty(JsonNode? node, string property)
    {
        return node?[property] as JsonArray ?? new JsonArray();
    }

    private static int? IntProperty(JsonNode? node, params string[] properties)
    {
        foreach (var property in properties)
        {
            var value = Text(node?[property]);
            if (int.TryParse(value, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? Text(JsonNode? node)
    {
        return node switch
        {
            null => null,
            JsonValue value => value.ToString(),
            _ => node.ToJsonString()
        };
    }

    private static bool IsConfigured(string? value) => !string.IsNullOrWhiteSpace(value);

    private static ProviderSnapshot ProviderStatus(string provider, string status, string detail) => new(provider, status, detail);

    private static object[] SeverityCounts(IEnumerable<object>? items = null)
    {
        var rows = items?.ToArray() ?? Array.Empty<object>();
        return new[] { "CRITICAL", "HIGH", "MODERATE", "MEDIUM", "LOW", "UNKNOWN" }
            .Select(severity => new { severity, count = rows.Count(item => SeverityOf(item) == severity) })
            .Cast<object>()
            .ToArray();
    }

    private static object[] SecretCounts(IEnumerable<object>? items = null)
    {
        var rows = items?.ToArray() ?? Array.Empty<object>();
        return new[]
        {
            new { key = "OPEN", label = "Open", count = rows.Length },
            new { key = "DEFAULT", label = "Default Pattern Alerts", count = rows.Count(item => TextFromObject(item, "category") is not "generic") },
            new { key = "GENERIC", label = "Generic Pattern Alerts", count = rows.Count(item => TextFromObject(item, "category") == "generic") }
        }.Cast<object>().ToArray();
    }

    private static string NormalizeSeverity(string? severity)
    {
        return (severity ?? "UNKNOWN").Trim().ToUpperInvariant() switch
        {
            "CRITICAL" or "BLOCKER" => "CRITICAL",
            "HIGH" or "ERROR" or "MAJOR" => "HIGH",
            "MODERATE" => "MODERATE",
            "MEDIUM" or "WARNING" or "MINOR" => "MEDIUM",
            "LOW" or "NOTE" or "INFO" => "LOW",
            "OPEN" => "OPEN",
            _ => "UNKNOWN"
        };
    }

    private static string SeverityOf(object item) => TextFromObject(item, "severity") ?? "UNKNOWN";

    private static string StatusOf(object? item)
    {
        if (item is JsonNode node)
        {
            return (Text(node["status"]) ?? string.Empty).Trim().ToUpperInvariant();
        }

        return (TextFromObject(item, "status") ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string? TextFromObject(object? item, string property)
    {
        return item?.GetType().GetProperty(property)?.GetValue(item)?.ToString();
    }

    private static bool HasType(object item, string type) => string.Equals(TextFromObject(item, "type"), type, StringComparison.OrdinalIgnoreCase);

    private static bool IsHealthyTool(JsonNode? item)
    {
        var status = StatusOf(item);
        return status is "SUCCESS" or "HEALTHY" or "PASSED" or "PASS";
    }

    private static bool IsFailedTool(JsonNode? item)
    {
        var status = StatusOf(item);
        return status is "FAILED" or "FAILURE" or "ERROR" or "CRITICAL";
    }

    private static bool NoFindings(SonarSnapshot sonar, GitHubSnapshot dependabot, GitHubSnapshot codeQl, GitHubSnapshot secretScanning, LintSnapshot lint, DependencyCheckSnapshot dependencyCheck, PlaywrightSnapshot playwright)
    {
        return sonar.VulnerabilityTotalCount == 0
            && sonar.BugTotalCount == 0
            && dependabot.Alerts.Length == 0
            && codeQl.Alerts.Length == 0
            && secretScanning.Alerts.Length == 0
            && lint.ErrorCount == 0
            && dependencyCheck.TotalFindings == 0
            && playwright.FailedTests == 0;
    }
}

sealed record ProviderSnapshot(string Provider, string Status, string Detail);

sealed record SonarSnapshot(
    ProviderSnapshot ProviderStatus,
    object[] Metrics,
    object[] Vulnerabilities,
    int VulnerabilityTotalCount,
    object[] Bugs,
    int BugTotalCount,
    object[] CodeSmells,
    int CodeSmellTotalCount,
    string? AnalysisDateUtc,
    string? AnalysisRevision,
    string? AnalysisVersion)
{
    public static SonarSnapshot Unavailable(string projectKey, string detail) => new(
        new ProviderSnapshot("SonarQube", "Unavailable", detail),
        Array.Empty<object>(),
        Array.Empty<object>(),
        0,
        Array.Empty<object>(),
        0,
        Array.Empty<object>(),
        0,
        null,
        null,
        projectKey);
}

sealed record GitHubSnapshot(ProviderSnapshot ProviderStatus, object[] SeverityCounts, object[] SecretCounts, object[] Alerts)
{
    public static GitHubSnapshot Unavailable(string provider, string detail) => new(
        new ProviderSnapshot(provider, "Unavailable", detail),
        CodeQualitySecurityServiceFallback.SeverityCounts(),
        CodeQualitySecurityServiceFallback.SecretCounts(),
        Array.Empty<object>());
}

sealed record LintSnapshot(
    string Status,
    string Detail,
    string? GeneratedAtUtc,
    int ErrorCount,
    int WarningCount,
    int ToolsTotal,
    int ToolsPassed,
    int ToolsFailed,
    int ToolsNotApplicable,
    int ToolsNotConfigured,
    object[] Findings,
    int TotalFindings)
{
    public static LintSnapshot Empty(string status, string detail) => new(status, detail, null, 0, 0, 0, 0, 0, 0, 0, Array.Empty<object>(), 0);
}

sealed record DependencyCheckSnapshot(string Status, string Detail, object[] SeverityCounts, int TotalFindings, object[] Findings)
{
    public static DependencyCheckSnapshot Empty(string status, string detail) => new(status, detail, CodeQualitySecurityServiceFallback.SeverityCounts(), 0, Array.Empty<object>());
}

sealed record CycloneDxSnapshot(string Status, string Detail, object[] Components, int ComponentCount);

sealed record PlaywrightSnapshot(string Status, string Detail, int TotalTests, int PassedTests, int FailedTests, int SkippedTests, object[] Results, object[] WorkflowContracts)
{
    public static PlaywrightSnapshot Empty(string status, string detail) => new(status, detail, 0, 0, 0, 0, Array.Empty<object>(), Array.Empty<object>());
}

static class CodeQualitySecurityServiceFallback
{
    public static object[] SeverityCounts() => new[] { "CRITICAL", "HIGH", "MODERATE", "MEDIUM", "LOW", "UNKNOWN" }
        .Select(severity => new { severity, count = 0 })
        .Cast<object>()
        .ToArray();

    public static object[] SecretCounts() => new[]
    {
        new { key = "OPEN", label = "Open", count = 0 },
        new { key = "DEFAULT", label = "Default Pattern Alerts", count = 0 },
        new { key = "GENERIC", label = "Generic Pattern Alerts", count = 0 }
    }.Cast<object>().ToArray();
}
