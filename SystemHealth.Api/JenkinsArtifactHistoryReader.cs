using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

sealed class JenkinsArtifactHistoryReader
{
    private const string ApplicationKey = "my-life-story-vault";
    private const string ApplicationLabel = "My Life Story Vault";
    private const string DevelopmentEnvironment = "Development";
    private const string HealthyStatus = "Healthy";
    private const string WarningStatus = "Warning";
    private const string UnknownStatus = "Unknown";

    private static readonly int[] ArtifactBuildCounts = [1, 10, 30, 50, 100];

    private readonly HttpClient _httpClient;

    public JenkinsArtifactHistoryReader(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ArtifactHistorySnapshot> GetAsync(
        SystemHealthOptions options,
        string? applicationKey,
        string? environment,
        int? buildCount,
        CancellationToken cancellationToken)
    {
        var selectedApplicationKey = string.IsNullOrWhiteSpace(applicationKey) ? ApplicationKey : applicationKey;
        var selectedEnvironment = string.IsNullOrWhiteSpace(environment) ? DevelopmentEnvironment : environment;
        var selectedBuildCount = ResolveArtifactBuildCount(buildCount);
        var jobName = string.IsNullOrWhiteSpace(options.Jenkins.JobName) ? "SystemHealth" : options.Jenkins.JobName;

        if (!string.Equals(selectedApplicationKey, ApplicationKey, StringComparison.OrdinalIgnoreCase))
        {
            return CreateUnavailableSnapshot(selectedApplicationKey, selectedEnvironment, selectedBuildCount, jobName, $"No Jenkins job is configured for application '{selectedApplicationKey}'.");
        }

        try
        {
            var artifacts = await TryLoadRemoteArtifactsAsync(options.Jenkins, jobName, selectedBuildCount, cancellationToken)
                ?? await TryLoadLocalArtifactsAsync(options, jobName, selectedBuildCount, cancellationToken);

            if (artifacts is null)
            {
                return CreateUnavailableSnapshot(
                    ApplicationKey,
                    selectedEnvironment,
                    selectedBuildCount,
                    jobName,
                    "Jenkins artifact history was not found in Jenkins API or the local Jenkins build archive.");
            }

            var orderedArtifacts = artifacts
                .OrderByDescending(artifact => artifact.Created)
                .ThenBy(artifact => artifact.FileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new ArtifactHistorySnapshot
            {
                GeneratedAtUtc = DateTime.UtcNow,
                Status = orderedArtifacts.Length == 0 ? WarningStatus : HealthyStatus,
                StatusDetail = orderedArtifacts.Length == 0
                    ? "No Jenkins artifacts were returned for the selected job/build range."
                    : "Jenkins artifact history loaded.",
                SelectedApplicationKey = ApplicationKey,
                SelectedEnvironment = selectedEnvironment,
                SelectedBuildCount = selectedBuildCount,
                JobName = jobName,
                Applications = Applications(),
                Environments = Environments(),
                BuildCounts = ArtifactBuildCounts,
                Artifacts = orderedArtifacts
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateUnavailableSnapshot(
                ApplicationKey,
                selectedEnvironment,
                selectedBuildCount,
                jobName,
                $"Unable to load Jenkins artifact history. {ex.Message}");
        }
    }

    private async Task<ArtifactHistoryItem[]?> TryLoadRemoteArtifactsAsync(
        JenkinsOptions options,
        string jobName,
        int buildCount,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl)
            || string.IsNullOrWhiteSpace(options.UserName)
            || string.IsNullOrWhiteSpace(options.ApiToken))
        {
            return null;
        }

        var builds = await GetRemoteBuildsAsync(options, jobName, cancellationToken);
        var artifacts = new List<ArtifactHistoryItem>();
        foreach (var build in builds.Take(buildCount))
        {
            var buildArtifacts = await GetRemoteBuildArtifactsAsync(options, jobName, build, cancellationToken);
            artifacts.AddRange(buildArtifacts);
        }

        return artifacts.ToArray();
    }

    private async Task<ArtifactHistoryItem[]?> TryLoadLocalArtifactsAsync(
        SystemHealthOptions options,
        string jobName,
        int buildCount,
        CancellationToken cancellationToken)
    {
        var buildsRoot = Path.Combine(options.CodeQualitySecurity.JenkinsHomeRoot, "jobs", jobName, "builds");
        if (!Directory.Exists(buildsRoot))
        {
            return null;
        }

        var buildDirectories = Directory.EnumerateDirectories(buildsRoot)
            .Select(path => new { Path = path, Number = int.TryParse(Path.GetFileName(path), out var number) ? number : -1 })
            .Where(item => item.Number >= 0)
            .OrderByDescending(item => item.Number)
            .Take(buildCount)
            .ToArray();

        var artifacts = new List<ArtifactHistoryItem>();
        foreach (var buildDirectory in buildDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var archiveRoot = Path.Combine(buildDirectory.Path, "archive");
            if (!Directory.Exists(archiveRoot))
            {
                continue;
            }

            var build = ReadLocalBuild(buildDirectory.Path, buildDirectory.Number);
            foreach (var artifactPath in Directory.EnumerateFiles(archiveRoot, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(archiveRoot, artifactPath).Replace('\\', '/');
                var fileInfo = new FileInfo(artifactPath);
                artifacts.Add(CreateArtifactItem(
                    options.Jenkins.BaseUrl,
                    jobName,
                    build.Number.ToString(),
                    build.Timestamp,
                    fileInfo.Name,
                    relativePath,
                    Math.Max(1, fileInfo.Length / 1024)));
            }
        }

        return artifacts.ToArray();
    }

    private async Task<JenkinsBuild[]> GetRemoteBuildsAsync(
        JenkinsOptions options,
        string jobName,
        CancellationToken cancellationToken)
    {
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
            .ToArray();
    }

    private async Task<ArtifactHistoryItem[]> GetRemoteBuildArtifactsAsync(
        JenkinsOptions options,
        string jobName,
        JenkinsBuild build,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{options.BaseUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(jobName)}/{build.Number}/api/json?tree=artifacts[fileName,relativePath]"));
        AddJenkinsAuthorization(request, options);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("artifacts", out var artifactsElement)
            || artifactsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var artifacts = new List<ArtifactHistoryItem>();
        foreach (var artifactElement in artifactsElement.EnumerateArray())
        {
            var fileName = artifactElement.TryGetProperty("fileName", out var fileNameElement)
                ? fileNameElement.GetString() ?? string.Empty
                : string.Empty;
            var relativePath = artifactElement.TryGetProperty("relativePath", out var relativePathElement)
                ? relativePathElement.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var downloadUrl = BuildJenkinsArtifactUrl(options.BaseUrl, jobName, build.Number.ToString(), relativePath);
            var sizeKb = await GetRemoteArtifactSizeKbAsync(options, downloadUrl, cancellationToken);
            artifacts.Add(CreateArtifactItem(options.BaseUrl, jobName, build.Number.ToString(), build.Timestamp, fileName, relativePath, sizeKb));
        }

        return artifacts.ToArray();
    }

    private async Task<long> GetRemoteArtifactSizeKbAsync(
        JenkinsOptions options,
        string artifactUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(artifactUrl));
            AddJenkinsAuthorization(request, options);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode || !response.Content.Headers.ContentLength.HasValue)
            {
                return 0;
            }

            return Math.Max(1, response.Content.Headers.ContentLength.Value / 1024);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return 0;
        }
    }

    private static JenkinsBuild ReadRemoteBuild(JsonElement element)
    {
        var timestampMilliseconds = element.TryGetProperty("timestamp", out var timestampElement)
            ? timestampElement.GetInt64()
            : 0;

        return new JenkinsBuild(
            element.TryGetProperty("number", out var numberElement) ? numberElement.GetInt32() : 0,
            timestampMilliseconds <= 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeMilliseconds(timestampMilliseconds).LocalDateTime);
    }

    private static JenkinsBuild ReadLocalBuild(string buildDirectory, int buildNumber)
    {
        var buildXmlPath = Path.Combine(buildDirectory, "build.xml");
        if (!File.Exists(buildXmlPath))
        {
            return new JenkinsBuild(buildNumber, Directory.GetLastWriteTime(buildDirectory));
        }

        try
        {
            var buildXml = File.ReadAllText(buildXmlPath);
            var timestampText = Regex.Match(buildXml, @"(?m)^\s{2}<timestamp>(?<timestamp>\d+)</timestamp>\s*$").Groups["timestamp"].Value;
            var timestamp = long.TryParse(timestampText, out var timestampMilliseconds) && timestampMilliseconds > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestampMilliseconds).LocalDateTime
                : Directory.GetLastWriteTime(buildDirectory);

            return new JenkinsBuild(buildNumber, timestamp);
        }
        catch (IOException)
        {
            return new JenkinsBuild(buildNumber, Directory.GetLastWriteTime(buildDirectory));
        }
    }

    private static ArtifactHistoryItem CreateArtifactItem(
        string baseUrl,
        string jobName,
        string buildNumber,
        DateTime created,
        string fileName,
        string relativePath,
        long sizeKb)
    {
        var isRollbackArtifact = IsRollbackArtifactPath(relativePath);
        return new ArtifactHistoryItem
        {
            FileName = fileName,
            DisplayName = fileName,
            RelativePath = relativePath,
            ArtifactType = isRollbackArtifact ? "Rollback Snapshot" : "Build Artifact",
            IsRollbackArtifact = isRollbackArtifact,
            SizeKb = sizeKb,
            Size = FormatArtifactSize(sizeKb),
            Created = created,
            BuildNumber = buildNumber,
            DownloadUrl = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : BuildJenkinsArtifactUrl(baseUrl, jobName, buildNumber, relativePath),
            BuildUrl = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : BuildJenkinsBuildUrl(baseUrl, jobName, buildNumber),
            LogUrl = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : BuildJenkinsBuildUrl(baseUrl, jobName, buildNumber)
        };
    }

    private static bool IsRollbackArtifactPath(string relativePath)
    {
        var normalizedPath = relativePath.Replace('\\', '/');
        return normalizedPath.StartsWith("_rollback/", StringComparison.OrdinalIgnoreCase)
            && normalizedPath.Contains("/website-current/", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveArtifactBuildCount(int? requestedBuildCount)
    {
        var requested = requestedBuildCount.GetValueOrDefault(1);
        return ArtifactBuildCounts.Contains(requested) ? requested : 1;
    }

    private static string FormatArtifactSize(long sizeKb)
    {
        return sizeKb <= 0 ? UnknownStatus : $"{sizeKb:N0} KB";
    }

    private static string BuildJenkinsBuildUrl(string baseUrl, string jobName, string buildId)
    {
        return $"{baseUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(jobName)}/{Uri.EscapeDataString(buildId)}/";
    }

    private static string BuildJenkinsArtifactUrl(string baseUrl, string jobName, string buildId, string relativePath)
    {
        var encodedPath = string.Join("/", relativePath.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        return $"{baseUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(jobName)}/{Uri.EscapeDataString(buildId)}/artifact/{encodedPath}";
    }

    private static void AddJenkinsAuthorization(HttpRequestMessage request, JenkinsOptions options)
    {
        var credentialBytes = Encoding.ASCII.GetBytes($"{options.UserName}:{options.ApiToken}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentialBytes));
    }

    private static ArtifactHistorySnapshot CreateUnavailableSnapshot(
        string selectedApplicationKey,
        string selectedEnvironment,
        int selectedBuildCount,
        string jobName,
        string detail)
    {
        return new ArtifactHistorySnapshot
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Status = WarningStatus,
            StatusDetail = detail,
            SelectedApplicationKey = selectedApplicationKey,
            SelectedEnvironment = selectedEnvironment,
            SelectedBuildCount = selectedBuildCount,
            JobName = jobName,
            Applications = Applications(),
            Environments = Environments(),
            BuildCounts = ArtifactBuildCounts
        };
    }

    private static object[] Applications() => [new { key = ApplicationKey, label = ApplicationLabel }];
    private static string[] Environments() => [DevelopmentEnvironment];

    private sealed record JenkinsBuild(int Number, DateTime Timestamp);
}

sealed class ArtifactHistorySnapshot
{
    public DateTime GeneratedAtUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string StatusDetail { get; set; } = string.Empty;
    public string SelectedApplicationKey { get; set; } = string.Empty;
    public string SelectedEnvironment { get; set; } = string.Empty;
    public int SelectedBuildCount { get; set; }
    public string JobName { get; set; } = string.Empty;
    public object[] Applications { get; set; } = [];
    public string[] Environments { get; set; } = [];
    public int[] BuildCounts { get; set; } = [];
    public IReadOnlyList<ArtifactHistoryItem> Artifacts { get; set; } = [];
}

sealed class ArtifactHistoryItem
{
    public string FileName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string ArtifactType { get; set; } = string.Empty;
    public bool IsRollbackArtifact { get; set; }
    public string Size { get; set; } = string.Empty;
    public long SizeKb { get; set; }
    public DateTime Created { get; set; }
    public string BuildNumber { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string BuildUrl { get; set; } = string.Empty;
    public string LogUrl { get; set; } = string.Empty;
}
