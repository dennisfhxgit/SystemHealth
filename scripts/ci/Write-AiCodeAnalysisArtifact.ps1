param(
    [string]$Workspace = $env:WORKSPACE,
    [string]$ArtifactPath,
    [string]$SonarHostUrl = $env:SONAR_HOST_URL,
    [string]$SonarToken = $env:SONAR_TOKEN,
    [string]$SonarProjectKey = $env:SONAR_PROJECT_KEY,
    [string]$BuildNumber = $env:BUILD_NUMBER,
    [string]$Branch = $env:BRANCH_NAME_GOVERNED,
    [string]$Commit = $env:GIT_COMMIT
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($Workspace)) {
    $Workspace = (Get-Location).Path
}

if ([string]::IsNullOrWhiteSpace($ArtifactPath)) {
    $ArtifactPath = Join-Path $Workspace '_jenkins/ai-code-analysis.json'
}

function Convert-ToRepoPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ''
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetFullPath($Workspace).TrimEnd('\', '/')
    if ($fullPath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        return $fullPath.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
    }

    return $Path.Replace('\', '/')
}

function New-Finding {
    param(
        [string]$Severity,
        [string]$Confidence,
        [string]$Category,
        [string]$File,
        [string]$Line,
        [string]$Summary,
        [string]$Recommendation
    )

    [pscustomobject]@{
        severity = $Severity
        confidence = $Confidence
        category = $Category
        file = $File
        line = $Line
        summary = $Summary
        recommendation = $Recommendation
    }
}

function Add-RepositoryFinding {
    param(
        [System.Collections.Generic.List[object]]$Findings,
        [string]$Severity = 'Medium',
        [string]$Confidence = 'High',
        [string]$File,
        [int]$LineNumber = 0,
        [string]$Summary,
        [string]$Recommendation
    )

    $Findings.Add((New-Finding `
        -Severity $Severity `
        -Confidence $Confidence `
        -Category 'Repository Hygiene' `
        -File $File `
        -Line $(if ($LineNumber -gt 0) { [string]$LineNumber } else { '' }) `
        -Summary $Summary `
        -Recommendation $Recommendation))
}

function Add-SonarFindings {
    param([System.Collections.Generic.List[object]]$Findings)

    if ([string]::IsNullOrWhiteSpace($SonarHostUrl) `
        -or [string]::IsNullOrWhiteSpace($SonarToken) `
        -or [string]::IsNullOrWhiteSpace($SonarProjectKey)) {
        Add-RepositoryFinding `
            -Findings $Findings `
            -Severity 'Medium' `
            -Confidence 'High' `
            -File 'scripts/ci/Write-AiCodeAnalysisArtifact.ps1' `
            -Summary 'SonarQube inputs were not available while generating ai-code-analysis.json.' `
            -Recommendation 'Verify SONAR_HOST_URL, SONAR_TOKEN, and SONAR_PROJECT_KEY are configured in the Jenkins job.'
        return
    }

    $pair = "{0}:" -f $SonarToken
    $basic = [Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes($pair))
    $headers = @{
        Authorization = "Basic $basic"
        Accept = 'application/json'
    }
    $projectKey = [uri]::EscapeDataString($SonarProjectKey)
    $issuesUri = "{0}/api/issues/search?componentKeys={1}&resolved=false&ps=100" -f $SonarHostUrl.TrimEnd('/'), $projectKey
    $issues = Invoke-RestMethod -Method Get -Uri $issuesUri -Headers $headers

    foreach ($issue in @($issues.issues)) {
        $file = [string]$issue.component
        if ($file.StartsWith("$($SonarProjectKey):", [StringComparison]::OrdinalIgnoreCase)) {
            $file = $file.Substring($SonarProjectKey.Length + 1)
        }

        $Findings.Add((New-Finding `
            -Severity ([string]$issue.severity) `
            -Confidence 'SonarQube' `
            -Category ([string]$issue.type) `
            -File $file `
            -Line $(if ($issue.line) { [string]$issue.line } else { '' }) `
            -Summary ([string]$issue.message) `
            -Recommendation "Review SonarQube rule $($issue.rule)."))
    }
}

function Get-LineNumber {
    param(
        [string[]]$Lines,
        [string]$Pattern
    )

    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match $Pattern) {
            return $i + 1
        }
    }

    return 0
}

function Test-ProjectReference {
    param(
        [System.Collections.Generic.List[object]]$Findings,
        [string]$File,
        [string]$ReferencedPath,
        [int]$LineNumber
    )

    $normalized = $ReferencedPath.Trim().Trim('"', "'").Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        return
    }

    if (-not (Test-Path -LiteralPath (Join-Path $Workspace $normalized))) {
        Add-RepositoryFinding `
            -Findings $Findings `
            -Severity 'High' `
            -Confidence 'High' `
            -File $File `
            -LineNumber $LineNumber `
            -Summary "CI metadata references missing project '$normalized'." `
            -Recommendation 'Update the CI path to the current project layout or remove the stale job metadata.'
    }
}

function Add-WorkflowProjectPathFindings {
    param([System.Collections.Generic.List[object]]$Findings)

    $workflowRoot = Join-Path $Workspace '.github/workflows'
    if (-not (Test-Path -LiteralPath $workflowRoot)) {
        return
    }

    $workflowFiles = Get-ChildItem -LiteralPath $workflowRoot -File -Include '*.yml', '*.yaml' -Recurse
    foreach ($file in $workflowFiles) {
        $repoPath = Convert-ToRepoPath $file.FullName
        $lines = Get-Content -LiteralPath $file.FullName
        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($match in [regex]::Matches($lines[$i], '[A-Za-z0-9_.-]+(?:[/\\][A-Za-z0-9_.-]+)+\.csproj')) {
                Test-ProjectReference `
                    -Findings $Findings `
                    -File $repoPath `
                    -ReferencedPath $match.Value `
                    -LineNumber ($i + 1)
            }
        }
    }
}

function Add-SolutionFindings {
    param([System.Collections.Generic.List[object]]$Findings)

    $solutionPath = Join-Path $Workspace 'SystemHealth.sln'
    if (-not (Test-Path -LiteralPath $solutionPath)) {
        Add-RepositoryFinding `
            -Findings $Findings `
            -Severity 'High' `
            -Confidence 'High' `
            -File 'SystemHealth.sln' `
            -Summary 'Authoritative solution file is missing.' `
            -Recommendation 'Restore the solution file or update Jenkins to the current build entry point.'
        return
    }

    $solutionText = Get-Content -LiteralPath $solutionPath -Raw
    $solutionLines = Get-Content -LiteralPath $solutionPath
    $projectMatches = [regex]::Matches($solutionText, 'Project\("\{[^"]+\}"\)\s*=\s*"(?<name>[^"]+)",\s*"(?<path>[^"]+)",\s*"\{(?<guid>[^}]+)\}"')
    $projectPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $projectGuids = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)

    foreach ($match in $projectMatches) {
        $relativePath = $match.Groups['path'].Value.Replace('\', '/')
        [void]$projectPaths.Add($relativePath)
        [void]$projectGuids.Add($match.Groups['guid'].Value)
        if (-not (Test-Path -LiteralPath (Join-Path $Workspace $relativePath))) {
            Add-RepositoryFinding `
                -Findings $Findings `
                -Severity 'High' `
                -Confidence 'High' `
                -File 'SystemHealth.sln' `
                -LineNumber (Get-LineNumber -Lines $solutionLines -Pattern ([regex]::Escape($match.Groups['path'].Value))) `
                -Summary "Solution references missing project '$relativePath'." `
                -Recommendation 'Remove the stale project entry or update it to the current project path.'
        }
    }

    $configuredGuids = [regex]::Matches($solutionText, '\{(?<guid>[A-Fa-f0-9-]{36})\}\.(Debug|Release)\|Any CPU')
    foreach ($guid in @($configuredGuids | ForEach-Object { $_.Groups['guid'].Value } | Select-Object -Unique)) {
        if (-not $projectGuids.Contains($guid)) {
            Add-RepositoryFinding `
                -Findings $Findings `
                -Severity 'Medium' `
                -Confidence 'High' `
                -File 'SystemHealth.sln' `
                -LineNumber (Get-LineNumber -Lines $solutionLines -Pattern ([regex]::Escape($guid))) `
                -Summary "Solution contains build configuration for unknown project GUID {$guid}." `
                -Recommendation 'Remove orphan ProjectConfigurationPlatforms entries so clean agents and contributors see the real project set.'
        }
    }

    $expectedSolutionProjects = @(
        'SystemHealth.Api/SystemHealth.Api.csproj'
    )

    foreach ($projectPath in $expectedSolutionProjects) {
        if ((Test-Path -LiteralPath (Join-Path $Workspace $projectPath)) -and -not $projectPaths.Contains($projectPath)) {
            Add-RepositoryFinding `
                -Findings $Findings `
                -Severity 'Medium' `
                -Confidence 'High' `
                -File 'SystemHealth.sln' `
                -Summary "Existing project '$projectPath' is not included in the solution." `
                -Recommendation 'Add the project to the solution or document why Jenkins builds it outside the solution.'
        }
    }
}

$findings = [System.Collections.Generic.List[object]]::new()
Add-SonarFindings -Findings $findings
Add-WorkflowProjectPathFindings -Findings $findings
Add-SolutionFindings -Findings $findings

if ([string]::IsNullOrWhiteSpace($Commit)) {
    $Commit = (& git -C $Workspace rev-parse HEAD).Trim()
}

if ([string]::IsNullOrWhiteSpace($Branch)) {
    $Branch = (& git -C $Workspace rev-parse --abbrev-ref HEAD).Trim()
}

$artifactDir = Split-Path -Parent $ArtifactPath
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

$payload = [pscustomobject]@{
    provider = 'SonarQube + Repository Hygiene'
    providerName = 'SonarQube + Repository Hygiene'
    buildNumber = $BuildNumber
    build = $BuildNumber
    commit = $Commit
    branch = $Branch
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    findings = @($findings)
}

$payload | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ArtifactPath -Encoding UTF8
Write-Host "Wrote AI code analysis artifact $ArtifactPath with $($findings.Count) finding(s)."
