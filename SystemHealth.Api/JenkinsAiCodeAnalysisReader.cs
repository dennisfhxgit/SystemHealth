using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

sealed class JenkinsAiCodeAnalysisReader
{
    private const string ApplicationKey = "my-life-story-vault";
    private const string ApplicationLabel = "My Life Story Vault";
    private const string DevelopmentEnvironment = "Development";
    private const string ArtifactFileName = "ai-code-analysis.json";
    private const string ProviderName = "SonarQube + Repository Hygiene";
    private const string HealthyStatus = "Healthy";
    private const string WarningStatus = "Warning";
    private const string UnavailableStatus = "Unavailable";

    private readonly HttpClient _httpClient;

    public JenkinsAiCodeAnalysisReader(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<AiCodeAnalysisSnapshot> GetAsync(
        SystemHealthOptions options,
        string? applicationKey,
        string? environment,
        string? buildId,
        CancellationToken cancellationToken)
    {
        var selectedApplicationKey = string.IsNullOrWhiteSpace(applicationKey) ? ApplicationKey : applicationKey;
        var selectedEnvironment = string.IsNullOrWhiteSpace(environment) ? DevelopmentEnvironment : environment;
        var selectedBuildId = string.IsNullOrWhiteSpace(buildId) ? "lastBuild" : buildId;
        var jobName = string.IsNullOrWhiteSpace(options.Jenkins.JobName) ? "SystemHealth" : options.Jenkins.JobName;

        if (!string.Equals(selectedApplicationKey, ApplicationKey, StringComparison.OrdinalIgnoreCase))
        {
            return CreateUnavailableSnapshot(selectedApplicationKey, selectedEnvironment, selectedBuildId, jobName, $"No Jenkins job is configured for application '{selectedApplicationKey}'.");
        }

        try
        {
            var artifact = await TryLoadConfiguredArtifactAsync(options, jobName, selectedBuildId, cancellationToken)
                ?? await TryLoadRemoteArtifactAsync(options.Jenkins, jobName, selectedBuildId, cancellationToken)
                ?? await TryLoadLocalArchivedArtifactAsync(options, jobName, selectedBuildId, cancellationToken)
                ?? await TryLoadWorkspaceArtifactAsync(options, jobName, selectedBuildId, cancellationToken);

            if (artifact is null)
            {
                return CreateUnavailableSnapshot(
                    ApplicationKey,
                    selectedEnvironment,
                    selectedBuildId,
                    jobName,
                    $"{ArtifactFileName} was not found on the selected Jenkins build.");
            }

            return ParseArtifact(artifact, selectedEnvironment, selectedBuildId, jobName, options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateUnavailableSnapshot(
                ApplicationKey,
                selectedEnvironment,
                selectedBuildId,
                jobName,
                $"Unable to load {ArtifactFileName}. {ex.Message}");
        }
    }

    private static async Task<AiCodeAnalysisArtifact?> TryLoadConfiguredArtifactAsync(
        SystemHealthOptions options,
        string jobName,
        string selectedBuildId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Artifacts.AiCodeAnalysisPath))
        {
            return null;
        }

        var path = Environment.ExpandEnvironmentVariables(options.Artifacts.AiCodeAnalysisPath);
        if (!File.Exists(path))
        {
            return null;
        }

        return new AiCodeAnalysisArtifact(
            await File.ReadAllTextAsync(path, cancellationToken),
            Path.GetFileName(path),
            selectedBuildId,
            string.Empty,
            string.Empty,
            BuildJenkinsBuildUrl(options.Jenkins.BaseUrl, jobName, selectedBuildId),
            string.Empty);
    }

    private async Task<AiCodeAnalysisArtifact?> TryLoadRemoteArtifactAsync(
        JenkinsOptions options,
        string jobName,
        string selectedBuildId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl)
            || string.IsNullOrWhiteSpace(options.UserName)
            || string.IsNullOrWhiteSpace(options.ApiToken))
        {
            return null;
        }

        var builds = await GetRemoteBuildsAsync(options, jobName, selectedBuildId, cancellationToken);
        foreach (var build in builds)
        {
            var artifact = await TryLoadRemoteBuildArtifactAsync(options, jobName, build, cancellationToken);
            if (artifact is not null)
            {
                return artifact;
            }
        }

        return null;
    }

    private static async Task<AiCodeAnalysisArtifact?> TryLoadLocalArchivedArtifactAsync(
        SystemHealthOptions options,
        string jobName,
        string selectedBuildId,
        CancellationToken cancellationToken)
    {
        var buildsRoot = Path.Combine(options.CodeQualitySecurity.JenkinsHomeRoot, "jobs", jobName, "builds");
        if (!Directory.Exists(buildsRoot))
        {
            return null;
        }

        var buildDirectories = ResolveLocalBuildDirectories(buildsRoot, selectedBuildId);
        foreach (var buildDirectory in buildDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var archiveRoot = Path.Combine(buildDirectory.Path, "archive");
            if (!Directory.Exists(archiveRoot))
            {
                continue;
            }

            var artifactPath = Directory
                .EnumerateFiles(archiveRoot, ArtifactFileName, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (artifactPath is null)
            {
                continue;
            }

            var build = ReadLocalBuild(buildDirectory.Path, buildDirectory.Number);
            var relativePath = Path.GetRelativePath(archiveRoot, artifactPath).Replace('\\', '/');
            return new AiCodeAnalysisArtifact(
                await File.ReadAllTextAsync(artifactPath, cancellationToken),
                relativePath,
                build.Number.ToString(),
                build.Result,
                string.Empty,
                BuildJenkinsBuildUrl(options.Jenkins.BaseUrl, jobName, build.Number.ToString()),
                BuildJenkinsArtifactUrl(options.Jenkins.BaseUrl, jobName, build.Number.ToString(), relativePath));
        }

        return null;
    }

    private static async Task<AiCodeAnalysisArtifact?> TryLoadWorkspaceArtifactAsync(
        SystemHealthOptions options,
        string jobName,
        string selectedBuildId,
        CancellationToken cancellationToken)
    {
        var artifactPath = Path.Combine(options.CodeQualitySecurity.JenkinsWorkspaceRoot, jobName, "_jenkins", ArtifactFileName);
        if (!File.Exists(artifactPath))
        {
            return null;
        }

        return new AiCodeAnalysisArtifact(
            await File.ReadAllTextAsync(artifactPath, cancellationToken),
            $"_jenkins/{ArtifactFileName}",
            selectedBuildId,
            string.Empty,
            string.Empty,
            BuildJenkinsBuildUrl(options.Jenkins.BaseUrl, jobName, selectedBuildId),
            string.Empty);
    }

    private async Task<JenkinsBuild[]> GetRemoteBuildsAsync(
        JenkinsOptions options,
        string jobName,
        string selectedBuildId,
        CancellationToken cancellationToken)
    {
        if (int.TryParse(selectedBuildId, out var requestedBuildNumber) && requestedBuildNumber > 0)
        {
            return [new JenkinsBuild(requestedBuildNumber, string.Empty)];
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{options.BaseUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(jobName)}/api/json?tree=builds[number,result,timestamp,url]"));
        AddJenkinsAuthorization(request, options);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("builds", out var buildsElement)
            || buildsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return buildsElement.EnumerateArray()
            .Select(ReadRemoteBuild)
            .Where(build => build.Number > 0)
            .OrderByDescending(build => build.Number)
            .Take(30)
            .ToArray();
    }

    private async Task<AiCodeAnalysisArtifact?> TryLoadRemoteBuildArtifactAsync(
        JenkinsOptions options,
        string jobName,
        JenkinsBuild build,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{options.BaseUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(jobName)}/{build.Number}/api/json?tree=number,result,artifacts[fileName,relativePath]"));
        AddJenkinsAuthorization(request, options);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var buildStatus = GetString(document.RootElement, "result");
        if (!document.RootElement.TryGetProperty("artifacts", out var artifactsElement)
            || artifactsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var artifactElement in artifactsElement.EnumerateArray())
        {
            var fileName = GetString(artifactElement, "fileName");
            var relativePath = GetString(artifactElement, "relativePath");
            if (!string.Equals(fileName, ArtifactFileName, StringComparison.OrdinalIgnoreCase)
                && !relativePath.EndsWith($"/{ArtifactFileName}", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(relativePath, ArtifactFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var artifactUrl = BuildJenkinsArtifactUrl(options.BaseUrl, jobName, build.Number.ToString(), relativePath);
            using var artifactRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(artifactUrl));
            AddJenkinsAuthorization(artifactRequest, options);
            using var artifactResponse = await _httpClient.SendAsync(artifactRequest, cancellationToken);
            artifactResponse.EnsureSuccessStatusCode();

            return new AiCodeAnalysisArtifact(
                await artifactResponse.Content.ReadAsStringAsync(cancellationToken),
                relativePath,
                build.Number.ToString(),
                buildStatus,
                string.Empty,
                BuildJenkinsBuildUrl(options.BaseUrl, jobName, build.Number.ToString()),
                artifactUrl);
        }

        return null;
    }

    private static AiCodeAnalysisSnapshot ParseArtifact(
        AiCodeAnalysisArtifact artifact,
        string selectedEnvironment,
        string selectedBuildId,
        string jobName,
        SystemHealthOptions options)
    {
        using var document = JsonDocument.Parse(artifact.Content);
        var root = document.RootElement;
        var findings = ReadFindings(root);
        var severityCounts = ReadSeverityCounts(root, findings);
        var buildNumber = FirstNonEmpty(GetString(root, "buildNumber"), GetString(root, "build"), artifact.BuildNumber);
        var providerName = FirstNonEmpty(GetString(root, "providerName"), GetString(root, "provider"), ProviderName);
        var commit = GetString(root, "commit");
        var branch = GetString(root, "branch");
        var status = findings.Length == 0 ? HealthyStatus : WarningStatus;
        var checks = new List<AiCodeAnalysisCheck>
        {
            new()
            {
                Name = "AI analysis artifact",
                Category = "Jenkins",
                Status = HealthyStatus,
                Detail = $"Jenkins {ArtifactFileName} artifact loaded."
            },
            new()
            {
                Name = "Result ingestion",
                Category = "Analysis",
                Status = findings.Length == 0 ? HealthyStatus : WarningStatus,
                Detail = findings.Length == 0
                    ? "AI code analysis completed with no findings."
                    : $"AI code analysis reported {findings.Length} finding(s)."
            },
            new()
            {
                Name = "Repository Scope",
                Category = "Source",
                Status = HealthyStatus,
                Detail = $"{options.Repository.Owner}/{options.Repository.Name}"
            }
        };

        if (findings.Any(finding => string.Equals(finding.Category, "Repository Hygiene", StringComparison.OrdinalIgnoreCase)))
        {
            checks.Add(new AiCodeAnalysisCheck
            {
                Name = "Repository hygiene",
                Category = "Source",
                Status = WarningStatus,
                Detail = "Repository hygiene findings are present in the AI code analysis artifact."
            });
        }

        return new AiCodeAnalysisSnapshot
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Status = status,
            StatusDetail = findings.Length == 0
                ? "AI code analysis artifact loaded with no findings."
                : $"AI code analysis artifact loaded with {findings.Length} finding(s).",
            SelectedApplicationKey = ApplicationKey,
            SelectedEnvironment = selectedEnvironment,
            BuildId = selectedBuildId,
            BuildNumber = buildNumber,
            BuildStatus = artifact.BuildStatus,
            JobName = jobName,
            BuildUrl = artifact.BuildUrl,
            ArtifactUrl = artifact.ArtifactUrl,
            ProviderName = providerName,
            LegacySourceStatus = GetString(root, "legacySourceStatus"),
            Commit = commit,
            Branch = branch,
            Applications = Applications(),
            Environments = Environments(),
            SeverityCounts = severityCounts,
            Findings = findings,
            Checks = checks
        };
    }

    private static AiCodeAnalysisFinding[] ReadFindings(JsonElement root)
    {
        if (!root.TryGetProperty("findings", out var findingsElement)
            || findingsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return findingsElement.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.Object)
            .Select(element => new AiCodeAnalysisFinding
            {
                Severity = NormalizeAiSeverity(FirstNonEmpty(GetString(element, "severity"), GetString(element, "level"), "Info")),
                Confidence = GetString(element, "confidence"),
                Category = FirstNonEmpty(GetString(element, "category"), GetString(element, "type"), GetString(element, "rule")),
                File = FirstNonEmpty(GetString(element, "file"), GetString(element, "filePath"), GetString(element, "path")),
                Line = FirstNonEmpty(GetString(element, "line"), GetString(element, "lineNumber")),
                Summary = FirstNonEmpty(GetString(element, "summary"), GetString(element, "title"), GetString(element, "message"), GetString(element, "description")),
                Recommendation = FirstNonEmpty(GetString(element, "recommendation"), GetString(element, "suggestion"), GetString(element, "fix"))
            })
            .ToArray();
    }

    private static AiCodeAnalysisSeverityCount[] ReadSeverityCounts(JsonElement root, AiCodeAnalysisFinding[] findings)
    {
        if (root.TryGetProperty("severityCounts", out var countsElement)
            && countsElement.ValueKind == JsonValueKind.Array)
        {
            var counts = countsElement.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.Object)
                .Select(element => new AiCodeAnalysisSeverityCount
                {
                    Severity = NormalizeAiSeverity(GetString(element, "severity")),
                    Count = GetInt32(element, "count")
                })
                .Where(count => !string.IsNullOrWhiteSpace(count.Severity))
                .Concat(DefaultSeverityCounts())
                .GroupBy(count => count.Severity, StringComparer.OrdinalIgnoreCase)
                .Select(group => new AiCodeAnalysisSeverityCount { Severity = group.First().Severity, Count = group.Sum(count => count.Count) })
                .OrderBy(count => SeverityRank(count.Severity))
                .ThenBy(count => count.Severity, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (counts.Length > 0)
            {
                return counts;
            }
        }

        return findings
            .GroupBy(finding => NormalizeAiSeverity(finding.Severity), StringComparer.OrdinalIgnoreCase)
            .Select(group => new AiCodeAnalysisSeverityCount { Severity = group.Key, Count = group.Count() })
            .Concat(DefaultSeverityCounts())
            .GroupBy(count => count.Severity, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AiCodeAnalysisSeverityCount { Severity = group.First().Severity, Count = group.Sum(count => count.Count) })
            .OrderBy(count => SeverityRank(count.Severity))
            .ThenBy(count => count.Severity, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static AiCodeAnalysisSeverityCount[] DefaultSeverityCounts() =>
    [
        new() { Severity = "Critical", Count = 0 },
        new() { Severity = "High", Count = 0 },
        new() { Severity = "Medium", Count = 0 },
        new() { Severity = "Low", Count = 0 }
    ];

    private static AiCodeAnalysisSnapshot CreateUnavailableSnapshot(
        string selectedApplicationKey,
        string selectedEnvironment,
        string selectedBuildId,
        string jobName,
        string detail)
    {
        return new AiCodeAnalysisSnapshot
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Status = WarningStatus,
            StatusDetail = detail,
            SelectedApplicationKey = selectedApplicationKey,
            SelectedEnvironment = selectedEnvironment,
            BuildId = selectedBuildId,
            JobName = jobName,
            ProviderName = ProviderName,
            Applications = Applications(),
            Environments = Environments(),
            SeverityCounts = DefaultSeverityCounts(),
            Findings = [],
            Checks =
            [
                new()
                {
                    Name = "AI analysis artifact",
                    Category = "Jenkins",
                    Status = UnavailableStatus,
                    Detail = detail
                }
            ]
        };
    }

    private static LocalBuildDirectory[] ResolveLocalBuildDirectories(string buildsRoot, string selectedBuildId)
    {
        if (int.TryParse(selectedBuildId, out var requestedBuildNumber) && requestedBuildNumber > 0)
        {
            var requestedPath = Path.Combine(buildsRoot, requestedBuildNumber.ToString());
            return Directory.Exists(requestedPath) ? [new LocalBuildDirectory(requestedPath, requestedBuildNumber)] : [];
        }

        return Directory.EnumerateDirectories(buildsRoot)
            .Select(path => new LocalBuildDirectory(path, int.TryParse(Path.GetFileName(path), out var number) ? number : -1))
            .Where(item => item.Number >= 0)
            .OrderByDescending(item => item.Number)
            .Take(30)
            .ToArray();
    }

    private static LocalBuild ReadLocalBuild(string buildDirectory, int buildNumber)
    {
        var buildXmlPath = Path.Combine(buildDirectory, "build.xml");
        if (!File.Exists(buildXmlPath))
        {
            return new LocalBuild(buildNumber, string.Empty);
        }

        try
        {
            var buildXml = File.ReadAllText(buildXmlPath);
            var result = Regex.Match(buildXml, @"(?m)^\s{2}<result>(?<result>[^<]+)</result>\s*$").Groups["result"].Value;
            return new LocalBuild(buildNumber, result);
        }
        catch (IOException)
        {
            return new LocalBuild(buildNumber, string.Empty);
        }
    }

    private static JenkinsBuild ReadRemoteBuild(JsonElement element)
    {
        return new JenkinsBuild(
            element.TryGetProperty("number", out var numberElement) ? numberElement.GetInt32() : 0,
            GetString(element, "result"));
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static int GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => 0
        };
    }

    private static int SeverityRank(string severity)
    {
        return NormalizeAiSeverity(severity).ToUpperInvariant() switch
        {
            "CRITICAL" => 0,
            "HIGH" => 1,
            "MEDIUM" or "MODERATE" => 2,
            "LOW" => 3,
            "INFO" or "INFORMATIONAL" => 4,
            _ => 5
        };
    }

    private static string NormalizeAiSeverity(string severity)
    {
        return severity.Trim().ToUpperInvariant() switch
        {
            "BLOCKER" or "CRITICAL" => "Critical",
            "HIGH" or "MAJOR" => "High",
            "MEDIUM" or "MODERATE" or "MINOR" => "Medium",
            "LOW" or "INFO" or "INFORMATIONAL" => "Low",
            _ => string.IsNullOrWhiteSpace(severity) ? "Low" : severity.Trim()
        };
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string BuildJenkinsBuildUrl(string baseUrl, string jobName, string buildId)
    {
        return string.IsNullOrWhiteSpace(baseUrl)
            ? string.Empty
            : $"{baseUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(jobName)}/{Uri.EscapeDataString(buildId)}/";
    }

    private static string BuildJenkinsArtifactUrl(string baseUrl, string jobName, string buildId, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        var encodedPath = string.Join("/", relativePath.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        return $"{baseUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(jobName)}/{Uri.EscapeDataString(buildId)}/artifact/{encodedPath}";
    }

    private static void AddJenkinsAuthorization(HttpRequestMessage request, JenkinsOptions options)
    {
        var credentialBytes = Encoding.ASCII.GetBytes($"{options.UserName}:{options.ApiToken}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentialBytes));
    }

    private static AiCodeAnalysisApplication[] Applications() => [new() { Key = ApplicationKey, Label = ApplicationLabel }];
    private static string[] Environments() => [DevelopmentEnvironment];

    private sealed record AiCodeAnalysisArtifact(
        string Content,
        string RelativePath,
        string BuildNumber,
        string BuildStatus,
        string BuildCommit,
        string BuildUrl,
        string ArtifactUrl);

    private sealed record JenkinsBuild(int Number, string Result);
    private sealed record LocalBuildDirectory(string Path, int Number);
    private sealed record LocalBuild(int Number, string Result);
}

sealed class AiCodeAnalysisSnapshot
{
    public DateTime GeneratedAtUtc { get; set; }
    public string Status { get; set; } = "Warning";
    public string StatusDetail { get; set; } = string.Empty;
    public string SelectedApplicationKey { get; set; } = string.Empty;
    public string SelectedEnvironment { get; set; } = string.Empty;
    public string BuildId { get; set; } = "lastBuild";
    public string BuildNumber { get; set; } = string.Empty;
    public string BuildStatus { get; set; } = string.Empty;
    public string JobName { get; set; } = string.Empty;
    public string BuildUrl { get; set; } = string.Empty;
    public string ArtifactUrl { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ProviderDashboardUrl { get; set; } = string.Empty;
    public string LegacySourceStatus { get; set; } = string.Empty;
    public string Commit { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public IReadOnlyList<AiCodeAnalysisApplication> Applications { get; set; } = Array.Empty<AiCodeAnalysisApplication>();
    public IReadOnlyList<string> Environments { get; set; } = Array.Empty<string>();
    public IReadOnlyList<AiCodeAnalysisSeverityCount> SeverityCounts { get; set; } = Array.Empty<AiCodeAnalysisSeverityCount>();
    public IReadOnlyList<AiCodeAnalysisFinding> Findings { get; set; } = Array.Empty<AiCodeAnalysisFinding>();
    public IReadOnlyList<AiCodeAnalysisCheck> Checks { get; set; } = Array.Empty<AiCodeAnalysisCheck>();
}

sealed class AiCodeAnalysisApplication
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

sealed class AiCodeAnalysisSeverityCount
{
    public string Severity { get; set; } = string.Empty;
    public int Count { get; set; }
}

sealed class AiCodeAnalysisFinding
{
    public string Severity { get; set; } = string.Empty;
    public string Confidence { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public string Line { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
}

sealed class AiCodeAnalysisCheck
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Status { get; set; } = "Warning";
    public string Detail { get; set; } = string.Empty;
}
