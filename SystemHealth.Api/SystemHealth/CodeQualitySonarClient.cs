using System.Globalization;
using System.Text.Json;

namespace CRM.Application.SystemHealth;

internal sealed class CodeQualitySonarClient
{
    private const int IssueDetailPageSize = 100;

    private static readonly string[] MetricKeys =
    [
        "coverage",
        "alert_status",
        "bugs",
        "code_smells",
        "duplicated_lines_density",
        "security_hotspots",
        "security_rating",
        "vulnerabilities",
        "sqale_rating",
        "reliability_rating"
    ];

    private readonly HttpClient _httpClient;

    public CodeQualitySonarClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<SonarMetricDto[]> GetMetricsAsync(
        string sonarBaseUrl,
        string sonarComponent,
        CancellationToken cancellationToken)
    {
        var metricKeys = string.Join(",", MetricKeys);
        using var response = await _httpClient.GetAsync(
            $"{sonarBaseUrl}/measures/component?component={Uri.EscapeDataString(sonarComponent)}&metricKeys={Uri.EscapeDataString(metricKeys)}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("component", out var componentElement)
            || !componentElement.TryGetProperty("measures", out var measuresElement)
            || measuresElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SonarMetricDto>();
        }

        return measuresElement
            .EnumerateArray()
            .Select(CreateMetric)
            .OrderByDescending(metric => metric.Key == "vulnerabilities")
            .ThenBy(metric => Array.IndexOf(MetricKeys, metric.Key))
            .ToArray();
    }

    public async Task<SonarIssueSearchResult> GetIssuesAsync(
        string sonarBaseUrl,
        string sonarComponent,
        string type,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"{sonarBaseUrl}/issues/search?componentKeys={Uri.EscapeDataString(sonarComponent)}&types={type}&resolved=false&statuses=OPEN,CONFIRMED,REOPENED&p=1&ps={IssueDetailPageSize}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("issues", out var issuesElement)
            || issuesElement.ValueKind != JsonValueKind.Array)
        {
            return SonarIssueSearchResult.Empty;
        }

        var issues = issuesElement
            .EnumerateArray()
            .Select(issue => CreateIssue(issue, sonarBaseUrl, sonarComponent))
            .GroupBy(issue => issue.Key)
            .Select(group => group.First())
            .ToArray();

        return new SonarIssueSearchResult(issues, GetPagingTotal(document.RootElement) ?? issues.Length);
    }

    public async Task<SonarLatestAnalysisDto?> GetLatestAnalysisAsync(
        string sonarBaseUrl,
        string sonarComponent,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"{sonarBaseUrl}/project_analyses/search?project={Uri.EscapeDataString(sonarComponent)}&ps=1",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("analyses", out var analysesElement)
            || analysesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var latestAnalysis = analysesElement.EnumerateArray().FirstOrDefault();
        return latestAnalysis.ValueKind == JsonValueKind.Undefined
            ? null
            : CreateLatestAnalysis(latestAnalysis);
    }

    private static SonarMetricDto CreateMetric(JsonElement measure)
    {
        var metric = measure.TryGetProperty("metric", out var metricElement)
            ? metricElement.GetString() ?? string.Empty
            : string.Empty;

        return new SonarMetricDto
        {
            Key = metric,
            Label = GetMetricLabel(metric),
            Value = measure.TryGetProperty("value", out var valueElement) ? valueElement.GetString() ?? "0" : "0",
            BestValue = measure.TryGetProperty("bestValue", out var bestValueElement) && bestValueElement.GetBoolean()
        };
    }

    private static SonarIssueDto CreateIssue(JsonElement issue, string sonarBaseUrl, string sonarComponent)
    {
        var key = issue.TryGetProperty("key", out var keyElement) ? keyElement.GetString() ?? string.Empty : string.Empty;

        return new SonarIssueDto
        {
            Severity = issue.TryGetProperty("severity", out var severityElement) ? severityElement.GetString() ?? string.Empty : string.Empty,
            Message = issue.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? string.Empty : string.Empty,
            Component = issue.TryGetProperty("component", out var componentElement) ? componentElement.GetString() ?? string.Empty : string.Empty,
            Line = issue.TryGetProperty("line", out var lineElement) && lineElement.TryGetInt32(out var line) ? line : null,
            Key = key,
            Url = string.IsNullOrWhiteSpace(key)
                ? string.Empty
                : $"{sonarBaseUrl.Replace("/api", string.Empty).TrimEnd('/')}/project/issues?id={Uri.EscapeDataString(sonarComponent)}&issues={Uri.EscapeDataString(key)}"
        };
    }

    private static SonarLatestAnalysisDto CreateLatestAnalysis(JsonElement analysis)
    {
        return new SonarLatestAnalysisDto
        {
            DateUtc = ParseSonarDate(GetString(analysis, "date")),
            Revision = GetString(analysis, "revision"),
            Version = GetString(analysis, "projectVersion")
        };
    }

    private static DateTime? ParseSonarDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = NormalizeSonarOffset(value);
        return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;
    }

    private static string NormalizeSonarOffset(string value)
    {
        if (value.Length > 5
            && (value[^5] == '+' || value[^5] == '-')
            && char.IsDigit(value[^4])
            && char.IsDigit(value[^3])
            && char.IsDigit(value[^2])
            && char.IsDigit(value[^1]))
        {
            return $"{value[..^2]}:{value[^2..]}";
        }

        return value;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int? GetPagingTotal(JsonElement root)
    {
        return root.TryGetProperty("paging", out var pagingElement)
            && pagingElement.TryGetProperty("total", out var totalElement)
            && totalElement.TryGetInt32(out var total)
            ? Math.Max(total, 0)
            : null;
    }

    private static string GetMetricLabel(string metric)
    {
        return metric switch
        {
            "coverage" => "Coverage (%)",
            "alert_status" => "Quality Gate",
            "bugs" => "Bugs",
            "code_smells" => "Code Smells",
            "duplicated_lines_density" => "Duplication (%)",
            "security_hotspots" => "Security Hotspots",
            "security_rating" => "Security Rating",
            "vulnerabilities" => "Vulnerabilities",
            "sqale_rating" => "Maintainability Rating",
            "reliability_rating" => "Reliability Rating",
            _ => metric
        };
    }
}

internal sealed class SonarLatestAnalysisDto
{
    public DateTime? DateUtc { get; set; }
    public string Revision { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

internal sealed record SonarIssueSearchResult(SonarIssueDto[] Issues, int TotalCount)
{
    public static SonarIssueSearchResult Empty { get; } = new(Array.Empty<SonarIssueDto>(), 0);
    public int DisplayedCount => Issues.Length;
    public bool IsTruncated => TotalCount > DisplayedCount;
}
