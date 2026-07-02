param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,
    [string]$Workspace = $env:WORKSPACE,
    [string]$BuildNumber = $env:BUILD_NUMBER,
    [string]$Branch = "main",
    [string]$Commit = "",
    [string]$Environment = "Development",
    [string]$Application = "my-life-story-vault",
    [string]$DependencyCheckPath = "C:\OWASP\DependencyCheck\bin\dependency-check.bat",
    [int]$DefaultToolTimeoutSeconds = 300,
    [int]$RestoreTimeoutSeconds = 600,
    [int]$DependencyCheckTimeoutSeconds = 900
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($Workspace)) {
    $Workspace = (Get-Location).Path
}

$Workspace = [System.IO.Path]::GetFullPath($Workspace)
$SourceRoot = [System.IO.Path]::GetFullPath($SourceRoot)
$jenkinsRoot = Join-Path $Workspace "_jenkins"
$lintPath = Join-Path $jenkinsRoot "lint\lint-report.json"
$sbomDir = Join-Path $jenkinsRoot "sbom"
$dependencyCheckDir = Join-Path $jenkinsRoot "dependency-check"

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $lintPath), $sbomDir, $dependencyCheckDir | Out-Null

if ([string]::IsNullOrWhiteSpace($Commit)) {
    try {
        $Commit = (& git -C $SourceRoot rev-parse HEAD 2>$null).Trim()
    } catch {
        $Commit = ""
    }
}

function Invoke-CheckedTool {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Category,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$SuccessDetail,
        [int]$TimeoutSeconds = $DefaultToolTimeoutSeconds
    )

    $outputFile = [System.IO.Path]::GetTempFileName()
    $errorFile = [System.IO.Path]::GetTempFileName()
    try {
        if (-not (Test-Path -LiteralPath $WorkingDirectory)) {
            throw "Working directory was not found: $WorkingDirectory"
        }

        $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -WorkingDirectory $WorkingDirectory -NoNewWindow -PassThru -RedirectStandardOutput $outputFile -RedirectStandardError $errorFile
        $completed = $process.WaitForExit([Math]::Max(1, $TimeoutSeconds) * 1000)
        if (-not $completed) {
            try {
                $process.Kill($true)
            } catch {
                $process.Kill()
            }

            return [pscustomobject]@{
                tool = [pscustomobject]@{ name = $Name; category = $Category; status = "Warning"; errors = 1; warnings = 0; detail = "$Name exceeded the $TimeoutSeconds second timeout."; exitCode = -2 }
                findings = @([pscustomobject]@{ tool = $Name; severity = "Error"; ruleId = "command-timeout"; file = ""; line = $null; column = $null; message = "$Name exceeded the $TimeoutSeconds second timeout."; rawSummary = "$FilePath $($Arguments -join ' ')" })
            }
        }

        $stdout = Get-Content -LiteralPath $outputFile -Raw -ErrorAction SilentlyContinue
        $stderr = Get-Content -LiteralPath $errorFile -Raw -ErrorAction SilentlyContinue
        $summary = (($stdout, $stderr) -join [Environment]::NewLine).Trim()
        if ($process.ExitCode -eq 0) {
            return [pscustomobject]@{
                tool = [pscustomobject]@{ name = $Name; category = $Category; status = "Healthy"; errors = 0; warnings = 0; detail = $SuccessDetail; exitCode = 0 }
                findings = @()
            }
        }

        $message = if ([string]::IsNullOrWhiteSpace($summary)) { "$Name failed with exit code $($process.ExitCode)." } else { $summary }
        return [pscustomobject]@{
            tool = [pscustomobject]@{ name = $Name; category = $Category; status = "Warning"; errors = 1; warnings = 0; detail = "$Name failed. See findings for captured output."; exitCode = $process.ExitCode }
            findings = @([pscustomobject]@{ tool = $Name; severity = "Error"; ruleId = "command-exit-code"; file = ""; line = $null; column = $null; message = "$Name exited with code $($process.ExitCode)."; rawSummary = $message })
        }
    } catch {
        return [pscustomobject]@{
            tool = [pscustomobject]@{ name = $Name; category = $Category; status = "Warning"; errors = 1; warnings = 0; detail = "$Name could not start. See findings for captured error."; exitCode = -1 }
            findings = @([pscustomobject]@{ tool = $Name; severity = "Error"; ruleId = "command-startup-failure"; file = ""; line = $null; column = $null; message = "$Name could not start."; rawSummary = $_.Exception.Message })
        }
    } finally {
        Remove-Item -LiteralPath $outputFile, $errorFile -Force -ErrorAction SilentlyContinue
    }
}

function New-StaticTool {
    param([string]$Name, [string]$Category, [string]$Status, [string]$Detail)
    [pscustomobject]@{ name = $Name; category = $Category; status = $Status; errors = 0; warnings = 0; detail = $Detail; exitCode = 0 }
}

function Get-NpmCommand {
    $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if ($null -ne $npm) { return $npm.Source }
    $npm = Get-Command npm -ErrorAction SilentlyContinue
    if ($null -ne $npm) { return $npm.Source }
    throw "npm was not found on PATH."
}

function Write-CycloneDxBomFromPackageLocks {
    param([string]$Root, [string]$OutputPath)

    $components = New-Object System.Collections.Generic.List[object]
    $lockFiles = Get-ChildItem -LiteralPath $Root -Recurse -File -Filter "package-lock.json" |
        Where-Object { $_.FullName -notmatch "\\node_modules\\" }

    foreach ($lockFile in $lockFiles) {
        $lock = Get-Content -LiteralPath $lockFile.FullName -Raw | ConvertFrom-Json
        if ($null -eq $lock.packages) { continue }
        foreach ($property in $lock.packages.PSObject.Properties) {
            if ([string]::IsNullOrWhiteSpace($property.Name) -or $property.Name -notlike "node_modules/*") { continue }
            $packageName = $property.Name.Substring("node_modules/".Length)
            $version = [string]$property.Value.version
            if ([string]::IsNullOrWhiteSpace($packageName) -or [string]::IsNullOrWhiteSpace($version)) { continue }
            $components.Add([ordered]@{ type = "library"; name = $packageName; version = $version; purl = "pkg:npm/$packageName@$version"; "bom-ref" = "pkg:npm/$packageName@$version"; scope = "required" })
        }
    }

    [ordered]@{
        bomFormat = "CycloneDX"
        specVersion = "1.5"
        serialNumber = "urn:uuid:$([guid]::NewGuid())"
        version = 1
        metadata = [ordered]@{
            timestamp = (Get-Date).ToUniversalTime().ToString("O")
            component = [ordered]@{ type = "application"; name = "My-Life-Story-Vault"; version = $Commit }
        }
        components = @($components | Sort-Object { $_.purl } -Unique)
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
}

$npm = Get-NpmCommand
$backend = Join-Path $SourceRoot "backend"
$mobile = Join-Path $SourceRoot "mylifestory"
$toolResults = New-Object System.Collections.Generic.List[object]
$findings = New-Object System.Collections.Generic.List[object]

$checks = @(
    [pscustomobject]@{ Name = "Backend npm ci"; Category = "Dependency Restore"; FilePath = $npm; Arguments = @("ci", "--prefer-offline", "--no-audit", "--fund=false"); WorkingDirectory = $backend; SuccessDetail = "Backend dependencies restored from package-lock.json."; TimeoutSeconds = $RestoreTimeoutSeconds },
    [pscustomobject]@{ Name = "Mobile npm ci"; Category = "Dependency Restore"; FilePath = $npm; Arguments = @("ci", "--prefer-offline", "--no-audit", "--fund=false"); WorkingDirectory = $mobile; SuccessDetail = "Mobile dependencies restored from package-lock.json."; TimeoutSeconds = $RestoreTimeoutSeconds },
    [pscustomobject]@{ Name = "Backend ESLint"; Category = "Static Lint"; FilePath = $npm; Arguments = @("exec", "--", "eslint", "{src,apps,libs,test}/**/*.ts", "--max-warnings=0"); WorkingDirectory = $backend; SuccessDetail = "Backend ESLint completed with zero warnings."; TimeoutSeconds = $DefaultToolTimeoutSeconds },
    [pscustomobject]@{ Name = "Mobile Expo lint"; Category = "Static Lint"; FilePath = $npm; Arguments = @("run", "lint"); WorkingDirectory = $mobile; SuccessDetail = "Mobile Expo lint completed."; TimeoutSeconds = $DefaultToolTimeoutSeconds },
    [pscustomobject]@{ Name = "Backend tests with coverage"; Category = "Unit Tests"; FilePath = $npm; Arguments = @("run", "test:cov", "--", "--runInBand"); WorkingDirectory = $backend; SuccessDetail = "Backend Jest coverage tests completed."; TimeoutSeconds = $DefaultToolTimeoutSeconds },
    [pscustomobject]@{ Name = "Mobile tests with coverage"; Category = "Unit Tests"; FilePath = $npm; Arguments = @("run", "test:coverage", "--", "--runInBand"); WorkingDirectory = $mobile; SuccessDetail = "Mobile Jest coverage tests completed."; TimeoutSeconds = $DefaultToolTimeoutSeconds },
    [pscustomobject]@{ Name = "Backend build"; Category = "Build / Analyzers"; FilePath = $npm; Arguments = @("run", "build"); WorkingDirectory = $backend; SuccessDetail = "Backend Nest build completed."; TimeoutSeconds = $DefaultToolTimeoutSeconds },
    [pscustomobject]@{ Name = "Mobile TypeScript"; Category = "Type Safety"; FilePath = $npm; Arguments = @("exec", "--", "tsc", "--noEmit"); WorkingDirectory = $mobile; SuccessDetail = "Mobile TypeScript completed with no emit."; TimeoutSeconds = $DefaultToolTimeoutSeconds }
)

foreach ($check in $checks) {
    $result = Invoke-CheckedTool -Name $check.Name -Category $check.Category -FilePath $check.FilePath -Arguments $check.Arguments -WorkingDirectory $check.WorkingDirectory -SuccessDetail $check.SuccessDetail -TimeoutSeconds $check.TimeoutSeconds
    $toolResults.Add($result.tool)
    foreach ($finding in @($result.findings)) { $findings.Add($finding) }
}

$toolResults.Add((New-StaticTool -Name "Formatting" -Category "Formatting" -Status "NotApplicable" -Detail "No formatting gate is defined by the My Life Story Vault Jenkins instructions."))
$toolResults.Add((New-StaticTool -Name "Standards / Source Contracts" -Category "Standards / Source Contracts" -Status "NotApplicable" -Detail "No standalone source-contract gate is defined by the My Life Story Vault Jenkins instructions."))

Write-CycloneDxBomFromPackageLocks -Root $SourceRoot -OutputPath (Join-Path $sbomDir "bom.json")

if (Test-Path -LiteralPath $DependencyCheckPath) {
    $dependencyArguments = @("--project", "My-Life-Story-Vault", "--scan", (Join-Path $backend "package.json"), "--scan", (Join-Path $backend "package-lock.json"), "--scan", (Join-Path $mobile "package.json"), "--scan", (Join-Path $mobile "package-lock.json"), "--out", $dependencyCheckDir, "--format", "JSON", "--format", "HTML", "--format", "XML", "--format", "SARIF", "--noupdate")
    $dependencyResult = Invoke-CheckedTool -Name "OWASP Dependency-Check" -Category "Dependency Restore" -FilePath $DependencyCheckPath -Arguments $dependencyArguments -WorkingDirectory $SourceRoot -SuccessDetail "OWASP Dependency-Check completed for My Life Story Vault package inputs." -TimeoutSeconds $DependencyCheckTimeoutSeconds
    if ($dependencyResult.tool.status -ne "Healthy") {
        $toolResults.Add($dependencyResult.tool)
        foreach ($finding in @($dependencyResult.findings)) { $findings.Add($finding) }
    }
} else {
    $toolResults.Add([pscustomobject]@{ name = "OWASP Dependency-Check"; category = "Dependency Restore"; status = "Warning"; errors = 1; warnings = 0; detail = "OWASP Dependency-Check was not found at $DependencyCheckPath."; exitCode = 1 })
}

$errorCount = @($findings | Where-Object { $_.severity -eq "Error" }).Count
$warningCount = @($findings | Where-Object { $_.severity -eq "Warning" }).Count
$failedTools = @($toolResults | Where-Object { $_.status -eq "Warning" }).Count
$passedTools = @($toolResults | Where-Object { $_.status -eq "Healthy" }).Count
$notApplicableTools = @($toolResults | Where-Object { $_.status -eq "NotApplicable" }).Count
$notConfiguredTools = @($toolResults | Where-Object { $_.status -eq "NotConfigured" }).Count
$statusDetail = if ($failedTools -eq 0) { "All My Life Story Vault code quality gates passed or were explicitly not applicable." } else { "$failedTools My Life Story Vault code quality gate(s) failed; $errorCount error finding(s) reported." }

[ordered]@{
    status = if ($failedTools -eq 0) { "Healthy" } else { "Warning" }
    statusDetail = $statusDetail
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    application = $Application
    environment = $Environment
    build = $BuildNumber
    branch = $Branch
    commit = $Commit
    totalFindings = $findings.Count
    errorCount = $errorCount
    warningCount = $warningCount
    toolsTotal = $toolResults.Count
    toolsPassed = $passedTools
    toolsFailed = $failedTools
    toolsNotApplicable = $notApplicableTools
    toolsNotConfigured = $notConfiguredTools
    tools = @($toolResults)
    findings = @($findings)
} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $lintPath -Encoding UTF8

Write-Host "Wrote lint report to $lintPath"
Write-Host "Wrote CycloneDX SBOM to $(Join-Path $sbomDir "bom.json")"

if ($failedTools -gt 0) {
    exit 1
}
