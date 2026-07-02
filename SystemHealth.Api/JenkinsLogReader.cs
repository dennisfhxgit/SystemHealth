using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

sealed class JenkinsLogReader
{
    private const string ApplicationKey = "my-life-story-vault";
    private const string ApplicationLabel = "My Life Story Vault";
    private const string DevelopmentEnvironment = "Development";
    private const string LastBuildId = "lastBuild";
    private const string HealthyStatus = "Healthy";
    private const string WarningStatus = "Warning";
    private const string SuccessStatus = "Success";
    private const string FailureStatus = "Failure";
    private const string RollbackStatus = "Rollback";
    private const string SmokeFailStatus = "Smoke Fail";
    private const string AbortedStatus = "Aborted";
    private const string RunningStatus = "Running";
    private const int DefaultMaxLogCharacters = 60000;

    private static readonly Regex JenkinsConsoleNoteRegex = new(@"\x1B\[8mha:.*?\x1B\[0m", RegexOptions.Compiled);
    private static readonly Regex AnsiSequenceRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;

    public JenkinsLogReader(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<JenkinsLogSnapshot> GetAsync(
        SystemHealthOptions options,
        string? applicationKey,
        string? environment,
        string? buildId,
        CancellationToken cancellationToken)
    {
        var selectedApplicationKey = string.IsNullOrWhiteSpace(applicationKey) ? ApplicationKey : applicationKey;
        var selectedEnvironment = string.IsNullOrWhiteSpace(environment) ? DevelopmentEnvironment : environment;
        var selectedBuildId = string.IsNullOrWhiteSpace(buildId) ? LastBuildId : buildId;
        var jobName = string.IsNullOrWhiteSpace(options.Jenkins.JobName) ? "SystemHealth" : options.Jenkins.JobName;
        var maxCharacters = NormalizeMaxLogCharacters(options.Jenkins.MaxLogCharacters);

        if (!string.Equals(selectedApplicationKey, ApplicationKey, StringComparison.OrdinalIgnoreCase))
        {
            return CreateUnavailableSnapshot(
                selectedApplicationKey,
                selectedEnvironment,
                selectedBuildId,
                jobName,
                maxCharacters,
                $"No Jenkins job is configured for application '{selectedApplicationKey}'.");
        }

        try
        {
            var report = await TryLoadRemoteLogAsync(options.Jenkins, jobName, selectedBuildId, cancellationToken)
                ?? await TryLoadLocalLogAsync(options, jobName, selectedBuildId, cancellationToken);

            if (report is null)
            {
                return CreateUnavailableSnapshot(
                    ApplicationKey,
                    selectedEnvironment,
                    selectedBuildId,
                    jobName,
                    maxCharacters,
                    "Jenkins log was not found in Jenkins consoleText or the local Jenkins build log archive.");
            }

            var logText = SanitizeLog(report.LogText);
            var isTruncated = logText.Length > maxCharacters;
            if (isTruncated)
            {
                logText = logText[..maxCharacters];
            }

            var buildStatus = NormalizeStatus(report.Result);
            var isProblemBuild = buildStatus is FailureStatus or RollbackStatus or SmokeFailStatus or AbortedStatus;

            return new JenkinsLogSnapshot
            {
                GeneratedAtUtc = DateTime.UtcNow,
                Status = isProblemBuild ? WarningStatus : HealthyStatus,
                StatusDetail = BuildJenkinsLogStatusDetail(buildStatus, isTruncated, maxCharacters),
                SelectedApplicationKey = ApplicationKey,
                SelectedEnvironment = selectedEnvironment,
                Applications = Applications(),
                Environments = Environments(),
                JobName = jobName,
                BuildId = selectedBuildId == LastBuildId && report.BuildNumber > 0 ? report.BuildNumber.ToString() : selectedBuildId,
                BuildStatus = buildStatus,
                LogText = logText,
                JenkinsUrl = report.Url,
                IsTruncated = isTruncated,
                MaxCharacters = maxCharacters
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateUnavailableSnapshot(
                ApplicationKey,
                selectedEnvironment,
                selectedBuildId,
                jobName,
                maxCharacters,
                $"Unable to load Jenkins log. {ex.Message}");
        }
    }

    private async Task<JenkinsLogReport?> TryLoadRemoteLogAsync(
        JenkinsOptions options,
        string jobName,
        string buildId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl)
            || string.IsNullOrWhiteSpace(options.UserName)
            || string.IsNullOrWhiteSpace(options.ApiToken))
        {
            return null;
        }

        var build = await GetRemoteBuildAsync(options, jobName, buildId, cancellationToken);
        var logText = await GetRemoteConsoleTextAsync(options, jobName, buildId, cancellationToken);
        var fallbackUrl = BuildJenkinsBuildUrl(options.BaseUrl, jobName, buildId);

        return new JenkinsLogReport(
            build.Number,
            build.Result,
            string.IsNullOrWhiteSpace(build.Url) ? fallbackUrl : build.Url,
            logText);
    }

    private async Task<JenkinsLogReport?> TryLoadLocalLogAsync(
        SystemHealthOptions options,
        string jobName,
        string buildId,
        CancellationToken cancellationToken)
    {
        var buildsRoot = Path.Combine(options.CodeQualitySecurity.JenkinsHomeRoot, "jobs", jobName, "builds");
        if (!Directory.Exists(buildsRoot))
        {
            return null;
        }

        var buildDirectory = ResolveLocalBuildDirectory(buildsRoot, buildId);
        if (buildDirectory is null)
        {
            return null;
        }

        var logPath = Path.Combine(buildDirectory, "log");
        if (!File.Exists(logPath))
        {
            return null;
        }

        var build = ReadLocalBuild(buildDirectory, buildId);
        var logText = await File.ReadAllTextAsync(logPath, cancellationToken);
        var buildUrl = string.IsNullOrWhiteSpace(options.Jenkins.BaseUrl)
            ? string.Empty
            : BuildJenkinsBuildUrl(options.Jenkins.BaseUrl, jobName, build.BuildNumber);

        return new JenkinsLogReport(
            int.TryParse(build.BuildNumber, out var buildNumber) ? buildNumber : 0,
            build.Result,
            buildUrl,
            logText);
    }

    private async Task<JenkinsBuild> GetRemoteBuildAsync(
        JenkinsOptions options,
        string jobName,
        string buildId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{options.BaseUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(jobName)}/{Uri.EscapeDataString(buildId)}/api/json?tree=number,result,timestamp,url"));
        AddJenkinsAuthorization(request, options);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        return new JenkinsBuild(
            root.TryGetProperty("number", out var numberElement) ? numberElement.GetInt32() : 0,
            root.TryGetProperty("result", out var resultElement) && resultElement.ValueKind != JsonValueKind.Null ? resultElement.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty);
    }

    private async Task<string> GetRemoteConsoleTextAsync(
        JenkinsOptions options,
        string jobName,
        string buildId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{options.BaseUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(jobName)}/{Uri.EscapeDataString(buildId)}/consoleText"));
        AddJenkinsAuthorization(request, options);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string? ResolveLocalBuildDirectory(string buildsRoot, string buildId)
    {
        if (!string.Equals(buildId, LastBuildId, StringComparison.OrdinalIgnoreCase))
        {
            var explicitBuildDirectory = Path.Combine(buildsRoot, buildId);
            return Directory.Exists(explicitBuildDirectory) ? explicitBuildDirectory : null;
        }

        return Directory.EnumerateDirectories(buildsRoot)
            .Select(path => new { Path = path, Number = int.TryParse(Path.GetFileName(path), out var number) ? number : -1 })
            .Where(item => item.Number >= 0)
            .OrderByDescending(item => item.Number)
            .Select(item => item.Path)
            .FirstOrDefault();
    }

    private static LocalBuild ReadLocalBuild(string buildDirectory, string requestedBuildId)
    {
        var buildNumber = Path.GetFileName(buildDirectory);
        var buildXmlPath = Path.Combine(buildDirectory, "build.xml");
        if (!File.Exists(buildXmlPath))
        {
            return new LocalBuild(string.IsNullOrWhiteSpace(buildNumber) ? requestedBuildId : buildNumber, string.Empty);
        }

        try
        {
            var buildXml = File.ReadAllText(buildXmlPath);
            var result = Regex.Match(buildXml, @"(?m)^\s{2}<result>(?<result>[^<]*)</result>\s*$").Groups["result"].Value;

            return new LocalBuild(string.IsNullOrWhiteSpace(buildNumber) ? requestedBuildId : buildNumber, result);
        }
        catch (IOException)
        {
            return new LocalBuild(string.IsNullOrWhiteSpace(buildNumber) ? requestedBuildId : buildNumber, string.Empty);
        }
    }

    private static string SanitizeLog(string logText)
    {
        if (string.IsNullOrEmpty(logText))
        {
            return string.Empty;
        }

        var withoutNotes = JenkinsConsoleNoteRegex.Replace(logText, string.Empty);
        return AnsiSequenceRegex.Replace(withoutNotes, string.Empty);
    }

    private static string BuildJenkinsLogStatusDetail(string buildStatus, bool isTruncated, int maxCharacters)
    {
        var detail = buildStatus switch
        {
            SuccessStatus => "Jenkins log loaded for a successful build.",
            RunningStatus => "Jenkins log loaded for a running build.",
            FailureStatus => "Jenkins log loaded for a failed build.",
            RollbackStatus => "Jenkins log loaded for a rollback build.",
            SmokeFailStatus => "Jenkins log loaded for a smoke-test failure build.",
            AbortedStatus => "Jenkins log loaded for an aborted build.",
            _ => $"Jenkins log loaded. Build status: {buildStatus}."
        };

        return isTruncated
            ? $"{detail} Output was truncated to {maxCharacters:N0} characters."
            : detail;
    }

    private static string NormalizeStatus(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return RunningStatus;
        }

        var normalized = result.Trim().Replace('_', ' ').ToUpperInvariant();
        if (normalized.Contains("SMOKE", StringComparison.OrdinalIgnoreCase))
        {
            return SmokeFailStatus;
        }

        if (normalized.Contains("ROLLBACK", StringComparison.OrdinalIgnoreCase))
        {
            return RollbackStatus;
        }

        return normalized switch
        {
            "SUCCESS" => SuccessStatus,
            "FAILURE" => FailureStatus,
            "FAILED" => FailureStatus,
            "UNSTABLE" => FailureStatus,
            "ABORTED" => AbortedStatus,
            _ => result.Trim()
        };
    }

    private static int NormalizeMaxLogCharacters(int maxCharacters)
    {
        return maxCharacters < 1000 ? DefaultMaxLogCharacters : maxCharacters;
    }

    private static string BuildJenkinsBuildUrl(string baseUrl, string jobName, string buildId)
    {
        return $"{baseUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(jobName)}/{Uri.EscapeDataString(buildId)}/";
    }

    private static void AddJenkinsAuthorization(HttpRequestMessage request, JenkinsOptions options)
    {
        var credentialBytes = Encoding.ASCII.GetBytes($"{options.UserName}:{options.ApiToken}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentialBytes));
    }

    private static JenkinsLogSnapshot CreateUnavailableSnapshot(
        string selectedApplicationKey,
        string selectedEnvironment,
        string buildId,
        string jobName,
        int maxCharacters,
        string detail)
    {
        return new JenkinsLogSnapshot
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Status = WarningStatus,
            StatusDetail = detail,
            SelectedApplicationKey = selectedApplicationKey,
            SelectedEnvironment = selectedEnvironment,
            BuildId = buildId,
            JobName = jobName,
            BuildStatus = "Unavailable",
            LogText = detail,
            MaxCharacters = maxCharacters,
            Applications = Applications(),
            Environments = Environments()
        };
    }

    private static object[] Applications() => [new { key = ApplicationKey, label = ApplicationLabel }];
    private static string[] Environments() => [DevelopmentEnvironment];

    private sealed record JenkinsBuild(int Number, string Result, string Url);
    private sealed record LocalBuild(string BuildNumber, string Result);
    private sealed record JenkinsLogReport(int BuildNumber, string Result, string Url, string LogText);
}

sealed class JenkinsLogSnapshot
{
    public DateTime GeneratedAtUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string StatusDetail { get; set; } = string.Empty;
    public string SelectedApplicationKey { get; set; } = string.Empty;
    public string SelectedEnvironment { get; set; } = string.Empty;
    public object[] Applications { get; set; } = [];
    public string[] Environments { get; set; } = [];
    public string JobName { get; set; } = string.Empty;
    public string BuildId { get; set; } = string.Empty;
    public string BuildStatus { get; set; } = string.Empty;
    public string LogText { get; set; } = string.Empty;
    public string JenkinsUrl { get; set; } = string.Empty;
    public bool IsTruncated { get; set; }
    public int MaxCharacters { get; set; }
}
