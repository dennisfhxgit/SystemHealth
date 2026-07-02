param(
    [string]$BaseUrl = $env:TEST12_URL,
    [string]$OutputPath,
    [int]$SamplesPerEndpoint = 3,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    throw 'BaseUrl is required.'
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $workspace = if ([string]::IsNullOrWhiteSpace($env:WORKSPACE)) { (Get-Location).Path } else { $env:WORKSPACE }
    $OutputPath = Join-Path $workspace 'TestResults/api-performance.junit.xml'
}

$BaseUrl = $BaseUrl.TrimEnd('/')
$SamplesPerEndpoint = [Math]::Max(1, $SamplesPerEndpoint)
$endpoints = @(
    '/health',
    '/api/system-health/code-quality-security',
    '/api/system-health/system-alerts',
    '/api/system-health/test-results',
    '/api/system-health/artifact-history',
    '/api/system-health/ai-code-analysis'
)

$results = New-Object 'System.Collections.Generic.List[object]'
foreach ($endpoint in $endpoints) {
    for ($sample = 1; $sample -le $SamplesPerEndpoint; $sample++) {
        $uri = "$BaseUrl$endpoint"
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $status = 'Passed'
        $failure = ''

        try {
            $response = Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec $TimeoutSeconds
            if ($response.StatusCode -ne 200) {
                $status = 'Failed'
                $failure = "$uri returned HTTP $($response.StatusCode)."
            }
        }
        catch {
            $status = 'Failed'
            $failure = $_.Exception.Message
        }
        finally {
            $stopwatch.Stop()
        }

        $results.Add([pscustomobject]@{
            Endpoint = $endpoint
            Sample = $sample
            DurationSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
            Status = $status
            Failure = $failure
        })
    }
}

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$settings = New-Object System.Xml.XmlWriterSettings
$settings.Indent = $true
$settings.Encoding = [System.Text.UTF8Encoding]::new($false)
$writer = [System.Xml.XmlWriter]::Create($OutputPath, $settings)
try {
    $failures = @($results | Where-Object { $_.Status -ne 'Passed' }).Count
    $totalTime = ($results | Measure-Object -Property DurationSeconds -Sum).Sum

    $writer.WriteStartDocument()
    $writer.WriteStartElement('testsuites')
    $writer.WriteAttributeString('name', 'SystemHealth API Performance')
    $writer.WriteAttributeString('tests', [string]$results.Count)
    $writer.WriteAttributeString('failures', [string]$failures)
    $writer.WriteAttributeString('errors', '0')
    $writer.WriteAttributeString('time', $totalTime.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture))

    $writer.WriteStartElement('testsuite')
    $writer.WriteAttributeString('name', 'SystemHealth API Performance')
    $writer.WriteAttributeString('tests', [string]$results.Count)
    $writer.WriteAttributeString('failures', [string]$failures)
    $writer.WriteAttributeString('errors', '0')
    $writer.WriteAttributeString('skipped', '0')
    $writer.WriteAttributeString('time', $totalTime.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture))

    foreach ($result in $results) {
        $writer.WriteStartElement('testcase')
        $writer.WriteAttributeString('classname', 'SystemHealth.Api.Performance')
        $writer.WriteAttributeString('name', "API Performance latency $($result.Endpoint) sample $($result.Sample)")
        $writer.WriteAttributeString('time', $result.DurationSeconds.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture))

        if ($result.Status -ne 'Passed') {
            $writer.WriteStartElement('failure')
            $writer.WriteAttributeString('message', $result.Failure)
            $writer.WriteString($result.Failure)
            $writer.WriteEndElement()
        }

        $writer.WriteEndElement()
    }

    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndDocument()
}
finally {
    $writer.Dispose()
}

if ($failures -gt 0) {
    throw "$failures API performance check(s) failed. Results written to $OutputPath"
}

Write-Host "Wrote SystemHealth API performance JUnit results to $OutputPath with $($results.Count) sample(s)."
