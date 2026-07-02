using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

sealed class JenkinsTestResultsReader
{
    private const string ApplicationKey = "my-life-story-vault";
    private const string ApplicationLabel = "My Life Story Vault";
    private const string DevelopmentEnvironment = "Development";
    private const string LastBuildId = "lastBuild";
    private const string HealthyStatus = "Healthy";
    private const string WarningStatus = "Warning";
    private const string PassTestStatus = "Pass";
    private const string FailTestStatus = "Fail";
    private const string SkipTestStatus = "Skip";
    private const string UnknownStatus = "Unknown";

    private static readonly string[] TestResultArtifactCandidates =
    [
        "TestResults/tests.trx",
        "TestResults/tests.junit.xml"
    ];

    private readonly HttpClient _httpClient;

    public JenkinsTestResultsReader(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<TestResultsSnapshot> GetAsync(
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

        if (!string.Equals(selectedApplicationKey, ApplicationKey, StringComparison.OrdinalIgnoreCase))
        {
            return CreateUnavailableSnapshot(selectedApplicationKey, selectedEnvironment, selectedBuildId, jobName, $"No Jenkins job is configured for application '{selectedApplicationKey}'.");
        }

        try
        {
            var report = await TryLoadRemoteReportAsync(options.Jenkins, jobName, selectedBuildId, cancellationToken)
                ?? await TryLoadLocalReportAsync(options, jobName, selectedBuildId, cancellationToken);

            if (report is null)
            {
                return CreateUnavailableSnapshot(
                    ApplicationKey,
                    selectedEnvironment,
                    selectedBuildId,
                    jobName,
                    "Jenkins test results were not found in Jenkins testReport or archived TestResults artifacts.");
            }

            return CreateSnapshot(ApplicationKey, selectedEnvironment, jobName, report);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateUnavailableSnapshot(
                ApplicationKey,
                selectedEnvironment,
                selectedBuildId,
                jobName,
                $"Unable to load Jenkins test results. {ex.Message}");
        }
    }

    private async Task<JenkinsTestReport?> TryLoadRemoteReportAsync(
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

        var metadata = await GetRemoteBuildMetadataAsync(options, jobName, buildId, cancellationToken);
        var cases = await GetRemoteBuildTestCasesAsync(options, jobName, buildId, cancellationToken);
        if (cases.Length == 0)
        {
            cases = await GetRemoteBuildArtifactTestCasesAsync(options, jobName, metadata.BuildId, cancellationToken);
        }

        return new JenkinsTestReport(metadata.BuildId, metadata.Commit, metadata.Branch, metadata.Timestamp, cases);
    }

    private async Task<JenkinsTestReport?> TryLoadLocalReportAsync(
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

        var metadata = ReadLocalBuildMetadata(buildDirectory, buildId);
        var cases = ReadLocalArchivedTestCases(buildDirectory);
        await Task.CompletedTask.WaitAsync(cancellationToken);
        return new JenkinsTestReport(metadata.BuildId, metadata.Commit, metadata.Branch, metadata.Timestamp, cases);
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

    private static JenkinsTestReportMetadata ReadLocalBuildMetadata(string buildDirectory, string requestedBuildId)
    {
        var buildXmlPath = Path.Combine(buildDirectory, "build.xml");
        if (!File.Exists(buildXmlPath))
        {
            return new JenkinsTestReportMetadata(Path.GetFileName(buildDirectory), string.Empty, string.Empty, null);
        }

        try
        {
            var document = XDocument.Load(buildXmlPath);
            var timestamp = document.Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "timestamp", StringComparison.Ordinal))
                ?.Value;
            var buildTime = long.TryParse(timestamp, out var timestampMilliseconds) && timestampMilliseconds > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestampMilliseconds).LocalDateTime
                : (DateTime?)null;

            var commit = document.Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "SHA1", StringComparison.Ordinal))
                ?.Value ?? string.Empty;
            var branch = document.Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "name", StringComparison.Ordinal)
                    && element.Parent is not null
                    && string.Equals(element.Parent.Name.LocalName, "branch", StringComparison.Ordinal))
                ?.Value ?? string.Empty;

            return new JenkinsTestReportMetadata(Path.GetFileName(buildDirectory), commit, branch, buildTime);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or InvalidOperationException)
        {
            return new JenkinsTestReportMetadata(requestedBuildId, string.Empty, string.Empty, null);
        }
    }

    private static JenkinsTestCase[] ReadLocalArchivedTestCases(string buildDirectory)
    {
        foreach (var relativePath in TestResultArtifactCandidates)
        {
            var artifactPath = Path.Combine(buildDirectory, "archive", relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(artifactPath))
            {
                continue;
            }

            var artifactText = File.ReadAllText(artifactPath);
            var cases = relativePath.EndsWith(".trx", StringComparison.OrdinalIgnoreCase)
                ? ReadTrxTestCases(artifactText)
                : ReadJunitTestCases(artifactText);
            if (cases.Length > 0)
            {
                return cases;
            }
        }

        return [];
    }

    private async Task<JenkinsTestReportMetadata> GetRemoteBuildMetadataAsync(
        JenkinsOptions options,
        string jobName,
        string buildId,
        CancellationToken cancellationToken)
    {
        return await GetJenkinsJsonAsync(
            options,
            $"job/{Uri.EscapeDataString(jobName)}/{Uri.EscapeDataString(buildId)}/api/json?tree=number,timestamp,actions[lastBuiltRevision[SHA1,branch[name]]],changeSet[items[commitId]]",
            root =>
            {
                var resolvedBuildId = root.TryGetProperty("number", out var numberElement)
                    ? numberElement.GetInt32().ToString()
                    : buildId;
                var timestamp = root.TryGetProperty("timestamp", out var timestampElement) && timestampElement.GetInt64() > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(timestampElement.GetInt64()).LocalDateTime
                    : (DateTime?)null;
                return new JenkinsTestReportMetadata(resolvedBuildId, ResolveCommit(root), ResolveBranch(root), timestamp);
            },
            cancellationToken);
    }

    private async Task<JenkinsTestCase[]> GetRemoteBuildTestCasesAsync(
        JenkinsOptions options,
        string jobName,
        string buildId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{options.BaseUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(jobName)}/{Uri.EscapeDataString(buildId)}/testReport/api/json?tree=suites[name,cases[className,name,status,duration,errorDetails]]"));
        AddJenkinsAuthorization(request, options);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ReadJsonTestCases(document.RootElement);
    }

    private async Task<JenkinsTestCase[]> GetRemoteBuildArtifactTestCasesAsync(
        JenkinsOptions options,
        string jobName,
        string buildId,
        CancellationToken cancellationToken)
    {
        foreach (var relativePath in TestResultArtifactCandidates)
        {
            var artifactUrl = $"{options.BaseUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(jobName)}/{Uri.EscapeDataString(buildId)}/artifact/{relativePath}";
            var artifactText = await TryGetRemoteArtifactTextAsync(options, artifactUrl, cancellationToken);
            if (string.IsNullOrWhiteSpace(artifactText))
            {
                continue;
            }

            var cases = relativePath.EndsWith(".trx", StringComparison.OrdinalIgnoreCase)
                ? ReadTrxTestCases(artifactText)
                : ReadJunitTestCases(artifactText);
            if (cases.Length > 0)
            {
                return cases;
            }
        }

        return [];
    }

    private async Task<string?> TryGetRemoteArtifactTextAsync(
        JenkinsOptions options,
        string artifactUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(artifactUrl));
            AddJenkinsAuthorization(request, options);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<T> GetJenkinsJsonAsync<T>(
        JenkinsOptions options,
        string relativeUrl,
        Func<JsonElement, T> read,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{options.BaseUrl.TrimEnd('/')}/{relativeUrl}"));
        AddJenkinsAuthorization(request, options);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return read(document.RootElement);
    }

    private static JenkinsTestCase[] ReadJsonTestCases(JsonElement root)
    {
        if (!TryGetTestSuites(root, out var suitesElement))
        {
            return [];
        }

        var cases = new List<JenkinsTestCase>();
        foreach (var suiteElement in suitesElement.EnumerateArray())
        {
            AddSuiteTestCases(suiteElement, cases);
        }

        return cases.ToArray();
    }

    private static bool TryGetTestSuites(JsonElement root, out JsonElement suitesElement)
    {
        suitesElement = default;
        if (root.TryGetProperty("suites", out suitesElement)
            && suitesElement.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        return root.TryGetProperty("testReport", out var testReportElement)
            && testReportElement.ValueKind == JsonValueKind.Object
            && testReportElement.TryGetProperty("suites", out suitesElement)
            && suitesElement.ValueKind == JsonValueKind.Array;
    }

    private static void AddSuiteTestCases(JsonElement suiteElement, ICollection<JenkinsTestCase> cases)
    {
        if (!suiteElement.TryGetProperty("cases", out var casesElement)
            || casesElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var suiteName = ReadString(suiteElement, "name");
        foreach (var caseElement in casesElement.EnumerateArray())
        {
            cases.Add(ReadTestCase(suiteName, caseElement));
        }
    }

    private static JenkinsTestCase ReadTestCase(string suiteName, JsonElement caseElement)
    {
        var className = ReadString(caseElement, "className");
        var name = ReadString(caseElement, "name");
        var status = NormalizeTestStatus(ReadString(caseElement, "status"));
        var duration = caseElement.TryGetProperty("duration", out var durationElement)
            && durationElement.TryGetDouble(out var durationValue)
                ? durationValue
                : 0;

        return new JenkinsTestCase(
            string.IsNullOrWhiteSpace(suiteName) ? className : suiteName,
            className,
            name,
            status,
            duration);
    }

    private static JenkinsTestCase[] ReadTrxTestCases(string trxXml)
    {
        try
        {
            var document = XDocument.Parse(trxXml);
            return document
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "UnitTestResult", StringComparison.Ordinal))
                .Select(element =>
                {
                    var testName = ReadXmlAttribute(element, "testName");
                    return new JenkinsTestCase(
                        ResolveSuiteName(testName),
                        ResolveClassName(testName),
                        ResolveTestName(testName),
                        NormalizeTestStatus(ReadXmlAttribute(element, "outcome")),
                        ReadDurationSeconds(ReadXmlAttribute(element, "duration")));
                })
                .Where(testCase => !string.IsNullOrWhiteSpace(testCase.Name))
                .ToArray();
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return [];
        }
    }

    private static JenkinsTestCase[] ReadJunitTestCases(string junitXml)
    {
        try
        {
            var document = XDocument.Parse(junitXml);
            return document
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "testcase", StringComparison.Ordinal))
                .Select(element =>
                {
                    var className = ReadXmlAttribute(element, "classname");
                    var name = ReadXmlAttribute(element, "name");
                    return new JenkinsTestCase(
                        FirstConfiguredValue(ReadXmlAttribute(element.Parent, "name"), className),
                        className,
                        name,
                        ResolveJunitStatus(element),
                        ReadDouble(ReadXmlAttribute(element, "time")));
                })
                .Where(testCase => !string.IsNullOrWhiteSpace(testCase.Name))
                .ToArray();
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return [];
        }
    }

    private static TestResultsSnapshot CreateSnapshot(
        string selectedApplicationKey,
        string selectedEnvironment,
        string jobName,
        JenkinsTestReport report)
    {
        var failedTests = report.Cases.Count(testCase => testCase.Status == FailTestStatus);
        var skippedTests = report.Cases.Count(testCase => testCase.Status == SkipTestStatus);
        var apiFunctionalResults = report.Cases
            .Where(testCase => !IsUiTest(testCase) && !IsPerformanceTest(testCase))
            .Select(testCase => new ApiFunctionalTestResult
            {
                BuildId = report.BuildId,
                Commit = report.Commit,
                Branch = report.Branch,
                Suite = testCase.Suite,
                Name = testCase.Name,
                Status = testCase.Status,
                Duration = FormatDuration(testCase.DurationSeconds),
                Timestamp = report.Timestamp
            })
            .ToArray();
        var uiResults = report.Cases
            .Where(IsUiTest)
            .Select(testCase => new UiTestResult
            {
                Scenario = testCase.Suite,
                Step = testCase.Name,
                Browser = ResolveBrowser(testCase),
                Status = testCase.Status,
                Duration = (int)Math.Ceiling(testCase.DurationSeconds),
                Screenshot = string.Empty
            })
            .ToArray();
        var performanceResults = BuildPerformanceResults(report.Cases.Where(IsPerformanceTest).ToArray());

        return new TestResultsSnapshot
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Status = report.Cases.Count == 0 || failedTests > 0 ? WarningStatus : HealthyStatus,
            StatusDetail = report.Cases.Count == 0
                ? "Jenkins returned no test cases for the selected build."
                : "Jenkins test results loaded.",
            SelectedApplicationKey = selectedApplicationKey,
            SelectedEnvironment = selectedEnvironment,
            BuildId = report.BuildId,
            JobName = jobName,
            TotalTests = report.Cases.Count,
            PassedTests = Math.Max(0, report.Cases.Count - failedTests - skippedTests),
            FailedTests = failedTests,
            SkippedTests = skippedTests,
            Applications = Applications(),
            Environments = Environments(),
            ApiFunctionalResults = apiFunctionalResults,
            ApiPerformanceResults = performanceResults,
            UiTestResults = uiResults
        };
    }

    private static TestResultsSnapshot CreateUnavailableSnapshot(
        string selectedApplicationKey,
        string selectedEnvironment,
        string buildId,
        string jobName,
        string detail)
    {
        return new TestResultsSnapshot
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Status = WarningStatus,
            StatusDetail = detail,
            SelectedApplicationKey = selectedApplicationKey,
            SelectedEnvironment = selectedEnvironment,
            BuildId = buildId,
            JobName = jobName,
            Applications = Applications(),
            Environments = Environments()
        };
    }

    private static string ResolveJunitStatus(XElement testCase)
    {
        if (testCase.Elements().Any(element =>
            string.Equals(element.Name.LocalName, "failure", StringComparison.Ordinal)
            || string.Equals(element.Name.LocalName, "error", StringComparison.Ordinal)))
        {
            return FailTestStatus;
        }

        return testCase.Elements().Any(element => string.Equals(element.Name.LocalName, "skipped", StringComparison.Ordinal))
            ? SkipTestStatus
            : PassTestStatus;
    }

    private static string NormalizeTestStatus(string status)
    {
        var normalized = status.Trim().ToUpperInvariant();
        return normalized switch
        {
            "PASSED" => PassTestStatus,
            "FIXED" => PassTestStatus,
            "REGRESSION" => FailTestStatus,
            "FAILED" => FailTestStatus,
            "SKIPPED" => SkipTestStatus,
            _ => string.IsNullOrWhiteSpace(status) ? UnknownStatus : status
        };
    }

    private static bool IsUiTest(JenkinsTestCase testCase)
    {
        var text = $"{testCase.Suite} {testCase.ClassName} {testCase.Name}";
        return ContainsAny(text, "ui", "e2e", "playwright", "selenium", "browser", "chromium", "firefox", "webkit");
    }

    private static bool IsPerformanceTest(JenkinsTestCase testCase)
    {
        var text = $"{testCase.Suite} {testCase.ClassName} {testCase.Name}";
        return ContainsAny(text, "performance", "load", "stress", "throughput", "latency", "response time");
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveBrowser(JenkinsTestCase testCase)
    {
        var text = $"{testCase.Suite} {testCase.ClassName} {testCase.Name}";
        if (text.Contains("firefox", StringComparison.OrdinalIgnoreCase))
        {
            return "Firefox";
        }

        if (text.Contains("webkit", StringComparison.OrdinalIgnoreCase))
        {
            return "WebKit";
        }

        if (text.Contains("chrome", StringComparison.OrdinalIgnoreCase)
            || text.Contains("chromium", StringComparison.OrdinalIgnoreCase))
        {
            return "Chromium";
        }

        return string.Empty;
    }

    private static ApiPerformanceTestResult[] BuildPerformanceResults(IReadOnlyList<JenkinsTestCase> performanceCases)
    {
        if (performanceCases.Count == 0)
        {
            return [];
        }

        return performanceCases
            .GroupBy(testCase => testCase.Suite)
            .Select(group =>
            {
                var cases = group.ToArray();
                var totalSeconds = cases.Sum(testCase => testCase.DurationSeconds);
                var failed = cases.Count(testCase => testCase.Status == FailTestStatus);
                return new ApiPerformanceTestResult
                {
                    Suite = group.Key,
                    Tps = totalSeconds > 0 ? (int)Math.Round(cases.Length / totalSeconds) : 0,
                    AvgResponse = (int)Math.Round(cases.Average(testCase => testCase.DurationSeconds) * 1000),
                    MaxResponse = (int)Math.Round(cases.Max(testCase => testCase.DurationSeconds) * 1000),
                    ErrorPercent = Math.Round((double)failed / cases.Length * 100, 2),
                    Throughput = cases.Length
                };
            })
            .ToArray();
    }

    private static string FormatDuration(double durationSeconds)
    {
        return durationSeconds >= 1
            ? $"{durationSeconds:0.###} sec"
            : $"{durationSeconds * 1000:0} ms";
    }

    private static string ResolveSuiteName(string testName)
    {
        var className = ResolveClassName(testName);
        return string.IsNullOrWhiteSpace(className) ? testName : className;
    }

    private static string ResolveClassName(string testName)
    {
        var lastSeparatorIndex = testName.LastIndexOf('.');
        return lastSeparatorIndex <= 0 ? string.Empty : testName[..lastSeparatorIndex];
    }

    private static string ResolveTestName(string testName)
    {
        var lastSeparatorIndex = testName.LastIndexOf('.');
        return lastSeparatorIndex < 0 || lastSeparatorIndex == testName.Length - 1 ? testName : testName[(lastSeparatorIndex + 1)..];
    }

    private static double ReadDurationSeconds(string duration)
    {
        return TimeSpan.TryParse(duration, out var parsed) ? parsed.TotalSeconds : ReadDouble(duration);
    }

    private static double ReadDouble(string value)
    {
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static string ReadXmlAttribute(XElement? element, string attributeName)
    {
        return element?.Attribute(attributeName)?.Value ?? string.Empty;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string FirstConfiguredValue(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string ResolveCommit(JsonElement root)
    {
        if (root.TryGetProperty("actions", out var actionsElement)
            && actionsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var actionElement in actionsElement.EnumerateArray())
            {
                if (actionElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (actionElement.TryGetProperty("lastBuiltRevision", out var revisionElement)
                    && revisionElement.ValueKind == JsonValueKind.Object
                    && revisionElement.TryGetProperty("SHA1", out var shaElement))
                {
                    return shaElement.GetString() ?? string.Empty;
                }
            }
        }

        if (root.TryGetProperty("changeSet", out var changeSetElement)
            && changeSetElement.TryGetProperty("items", out var itemsElement)
            && itemsElement.ValueKind == JsonValueKind.Array)
        {
            return itemsElement.EnumerateArray()
                .Select(item => item.TryGetProperty("commitId", out var commitElement) ? commitElement.GetString() ?? string.Empty : string.Empty)
                .FirstOrDefault(commit => !string.IsNullOrWhiteSpace(commit)) ?? string.Empty;
        }

        return string.Empty;
    }

    private static string ResolveBranch(JsonElement root)
    {
        if (!root.TryGetProperty("actions", out var actionsElement)
            || actionsElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var actionElement in actionsElement.EnumerateArray())
        {
            if (actionElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!actionElement.TryGetProperty("lastBuiltRevision", out var revisionElement)
                || revisionElement.ValueKind != JsonValueKind.Object
                || !revisionElement.TryGetProperty("branch", out var branchElement)
                || branchElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var branch = branchElement.EnumerateArray()
                .Select(item => item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
            if (!string.IsNullOrWhiteSpace(branch))
            {
                return branch;
            }
        }

        return string.Empty;
    }

    private static void AddJenkinsAuthorization(HttpRequestMessage request, JenkinsOptions options)
    {
        var credentialBytes = Encoding.ASCII.GetBytes($"{options.UserName}:{options.ApiToken}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentialBytes));
    }

    private static object[] Applications() => [new { key = ApplicationKey, label = ApplicationLabel }];
    private static string[] Environments() => [DevelopmentEnvironment];

    private sealed record JenkinsTestReportMetadata(string BuildId, string Commit, string Branch, DateTime? Timestamp);
    private sealed record JenkinsTestReport(string BuildId, string Commit, string Branch, DateTime? Timestamp, IReadOnlyList<JenkinsTestCase> Cases);
    private sealed record JenkinsTestCase(string Suite, string ClassName, string Name, string Status, double DurationSeconds);
}

sealed class TestResultsSnapshot
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
    public int TotalTests { get; set; }
    public int PassedTests { get; set; }
    public int FailedTests { get; set; }
    public int SkippedTests { get; set; }
    public IReadOnlyList<ApiFunctionalTestResult> ApiFunctionalResults { get; set; } = [];
    public IReadOnlyList<ApiPerformanceTestResult> ApiPerformanceResults { get; set; } = [];
    public IReadOnlyList<UiTestResult> UiTestResults { get; set; } = [];
}

sealed class ApiFunctionalTestResult
{
    public string BuildId { get; set; } = string.Empty;
    public string Commit { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string Suite { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public DateTime? Timestamp { get; set; }
}

sealed class ApiPerformanceTestResult
{
    public string Suite { get; set; } = string.Empty;
    public int Tps { get; set; }
    public int AvgResponse { get; set; }
    public int MaxResponse { get; set; }
    public double ErrorPercent { get; set; }
    public int Throughput { get; set; }
}

sealed class UiTestResult
{
    public string Scenario { get; set; } = string.Empty;
    public string Step { get; set; } = string.Empty;
    public string Browser { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Duration { get; set; }
    public string Screenshot { get; set; } = string.Empty;
}
