pipeline {
  agent {
    node {
      label 'built-in'
      customWorkspace 'C:/ProgramData/Jenkins/.jenkins/workspace/SystemHealth'
    }
  }

  options {
    disableConcurrentBuilds()
    skipDefaultCheckout(true)
  }

  environment {
    REPO_URL = 'https://github.com/dennisfhxgit/SystemHealth.git'
    BRANCH_NAME_GOVERNED = 'master'
    SOLUTION_PATH = 'SystemHealth.sln'
    API_PROJECT_PATH = 'SystemHealth.Api/SystemHealth.Api.csproj'
    PUBLISH_DIR = '_jenkins/publish/SystemHealth.Api'
    BUILD_CHECKOUT_COMMIT_FILE = '_jenkins/build-checkout-commit.txt'
    TEST12_PATH = 'W:/vhosts/fhx.co.nz/test12.fhx.co.nz'
    TEST12_APPPOOL = 'test12.fhx.co.nz(domain)(4.0)(pool)'
    TEST12_URL = 'https://test12.fhx.co.nz'
    SONAR_PROJECT_KEY = 'SystemHealth'
    SONAR_HOST_URL = 'https://sonarqube.fhx.co.nz'
    SONAR_EXCLUSIONS = '**/bin/**,**/obj/**,**/dist/**,**/node_modules/**,_jenkins/**,TestResults/**,coverage/**'
    SONAR_COVERAGE_EXCLUSIONS = '**/bin/**,**/obj/**,**/dist/**,**/node_modules/**,_jenkins/**,TestResults/**,coverage/**,SystemHealth.Api/Jenkins*Reader.cs,SystemHealth.Api/StandaloneSystemAlertsReader.cs,SystemHealth.Api/StandaloneCodeQualitySecurityEndpoint.cs,SystemHealth.Api/AdminEnvironment*.cs,SystemHealth.Api/Program.cs'
    SONAR_JS_LCOV_REPORT_PATHS = 'coverage/lcov.info'
    SONAR_TEST_INCLUSIONS = 'src/**/*.test.ts'
  }

  stages {
    stage('Checkout Repo') {
      steps {
        retry(3) {
          sleep time: 10, unit: 'SECONDS'
          checkout([
            $class: 'GitSCM',
            branches: [[name: '*/master']],
            userRemoteConfigs: [[
              url: env.REPO_URL,
              refspec: '+refs/heads/master:refs/remotes/origin/master'
            ]],
            extensions: [
              [$class: 'CloneOption', shallow: false, noTags: true, timeout: 20, honorRefspec: true],
              [$class: 'PruneStaleBranch']
            ]
          ])
        }
      }
    }

    stage('Provenance') {
      steps {
        powershell '''
        $ErrorActionPreference = 'Stop'

        $generatedPaths = @(
          '_publish',
          '_rollback',
          '_jenkins/_rollback',
          'dist',
          'build_provenance.env',
          'tsconfig.tsbuildinfo'
        )

        $workspaceRoot = [System.IO.Path]::GetFullPath($env:WORKSPACE).TrimEnd('\\', '/')
        foreach ($relativePath in $generatedPaths) {
          $fullPath = [System.IO.Path]::GetFullPath((Join-Path $env:WORKSPACE $relativePath))
          if (-not $fullPath.StartsWith($workspaceRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean path outside workspace: $fullPath"
          }

          if (Test-Path -LiteralPath $fullPath) {
            Remove-Item -LiteralPath $fullPath -Recurse -Force
            Write-Host "Removed stale generated path: $relativePath"
          }
        }

        $commit = (git rev-parse HEAD).Trim()
        if ([string]::IsNullOrWhiteSpace($commit)) {
          throw 'Checked-out commit could not be resolved.'
        }

        git fetch origin '+refs/heads/master:refs/remotes/origin/master' --prune
        $branchTip = (git rev-parse 'origin/master').Trim()
        if ([string]$commit -ne [string]$branchTip) {
          throw "Governance breach: checked-out commit $commit is not exactly origin/master at $branchTip"
        }

        $commitFile = Join-Path $env:WORKSPACE $env:BUILD_CHECKOUT_COMMIT_FILE
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $commitFile) | Out-Null
        Set-Content -LiteralPath $commitFile -Value $commit -Encoding ASCII -Force

        "GIT_COMMIT_FULL=$commit" | Set-Content -LiteralPath (Join-Path $env:WORKSPACE 'build_provenance.env') -Encoding UTF8
        "GIT_BRANCH_GOVERNED=master" | Add-Content -LiteralPath (Join-Path $env:WORKSPACE 'build_provenance.env') -Encoding UTF8
        "GIT_REPO_URL=$env:REPO_URL" | Add-Content -LiteralPath (Join-Path $env:WORKSPACE 'build_provenance.env') -Encoding UTF8
        "BUILD_ID_VALUE=$env:BUILD_TAG" | Add-Content -LiteralPath (Join-Path $env:WORKSPACE 'build_provenance.env') -Encoding UTF8

        Write-Host "Repository: $env:REPO_URL"
        Write-Host 'Branch: master'
        Write-Host "Commit: $commit"
        '''
      }
    }

    stage('Install Dependencies') {
      steps {
        bat 'npm ci'
      }
    }

    stage('Frontend Build And Tests') {
      steps {
        bat 'npm run typecheck'
        bat 'npm run lint'
        bat 'if not exist "%WORKSPACE%\\TestResults" mkdir "%WORKSPACE%\\TestResults"'
        bat 'npm run test:coverage -- --reporter=default --reporter=junit --outputFile="%WORKSPACE%\\TestResults\\tests.junit.xml"'
        bat 'npm run build'
      }
    }

    stage('Backend Build And Tests') {
      steps {
        withCredentials([string(credentialsId: 'sonar-token', variable: 'SONAR_TOKEN')]) {
          bat '''
          dotnet sonarscanner begin ^
            /k:"%SONAR_PROJECT_KEY%" ^
            /n:"SystemHealth" ^
            /d:sonar.host.url="%SONAR_HOST_URL%" ^
            /d:sonar.token="%SONAR_TOKEN%" ^
            /d:sonar.exclusions="%SONAR_EXCLUSIONS%" ^
            /d:sonar.coverage.exclusions="%SONAR_COVERAGE_EXCLUSIONS%" ^
            /d:sonar.javascript.lcov.reportPaths="%SONAR_JS_LCOV_REPORT_PATHS%" ^
            /d:sonar.test.inclusions="%SONAR_TEST_INCLUSIONS%" ^
            /d:sonar.qualitygate.wait=true ^
            /d:sonar.projectBaseDir="%CD%"
          if errorlevel 1 exit /b %errorlevel%

          dotnet restore "%SOLUTION_PATH%"
          if errorlevel 1 exit /b %errorlevel%

          dotnet build "%SOLUTION_PATH%" -c Release --no-restore
          if errorlevel 1 exit /b %errorlevel%

          dotnet test "%SOLUTION_PATH%" -c Release --no-build --logger "trx;LogFileName=tests.trx" --results-directory "%WORKSPACE%\\TestResults"
          if errorlevel 1 exit /b %errorlevel%

          dotnet sonarscanner end /d:sonar.token="%SONAR_TOKEN%"
          if errorlevel 1 exit /b %errorlevel%
          '''
        }
      }
    }

    stage('Code Quality Artifact Publish') {
      steps {
        bat '''
        "C:\\Program Files\\PowerShell\\7\\pwsh.exe" -NoProfile -ExecutionPolicy Bypass -File "%WORKSPACE%\\scripts\\ci\\Write-SystemHealthCodeQualityArtifacts.ps1" -Workspace "%WORKSPACE%" -OutputRoot "C:\\ProgramData\\Jenkins\\.jenkins\\fhx-system-health\\SystemHealth\\latest" -BuildNumber "%BUILD_NUMBER%" -BuildUrl "%BUILD_URL%" -Branch "master" -Commit "%GIT_COMMIT%"
        if errorlevel 1 exit /b %errorlevel%
        '''
        powershell '''
        $ErrorActionPreference = 'Stop'
        $manifest = 'C:/ProgramData/Jenkins/.jenkins/fhx-system-health/SystemHealth/latest/manifest.json'
        if (-not (Test-Path -LiteralPath $manifest)) {
          throw "Code Quality manifest was not created: $manifest"
        }

        $testResultsSource = Join-Path $env:WORKSPACE 'TestResults'
        $testResultsTarget = Join-Path (Split-Path -Parent $manifest) 'TestResults'
        $junitReport = Join-Path $testResultsSource 'tests.junit.xml'
        if (-not (Test-Path -LiteralPath $junitReport)) {
          throw "JUnit test report was not created before Code Quality artifact publish: $junitReport"
        }

        if (Test-Path -LiteralPath $testResultsTarget) {
          Remove-Item -LiteralPath $testResultsTarget -Recurse -Force
        }

        New-Item -ItemType Directory -Force -Path $testResultsTarget | Out-Null
        Copy-Item -Path (Join-Path $testResultsSource '*') -Destination $testResultsTarget -Recurse -Force

        '''
        withCredentials([string(credentialsId: 'sonar-token', variable: 'SONAR_TOKEN')]) {
          powershell '''
          $ErrorActionPreference = 'Stop'
          $manifest = 'C:/ProgramData/Jenkins/.jenkins/fhx-system-health/SystemHealth/latest/manifest.json'
          $aiCodeAnalysisArtifact = Join-Path $env:WORKSPACE '_jenkins/ai-code-analysis.json'
          & (Join-Path $env:WORKSPACE 'scripts/ci/Write-AiCodeAnalysisArtifact.ps1') `
            -Workspace $env:WORKSPACE `
            -ArtifactPath $aiCodeAnalysisArtifact `
            -SonarHostUrl $env:SONAR_HOST_URL `
            -SonarToken $env:SONAR_TOKEN `
            -SonarProjectKey $env:SONAR_PROJECT_KEY `
            -BuildNumber $env:BUILD_NUMBER `
            -Branch $env:BRANCH_NAME_GOVERNED `
            -Commit $env:GIT_COMMIT
          if (-not (Test-Path -LiteralPath $aiCodeAnalysisArtifact)) {
            throw "AI Code Analysis artifact was not created: $aiCodeAnalysisArtifact"
          }

          Copy-Item -LiteralPath $aiCodeAnalysisArtifact -Destination (Join-Path (Split-Path -Parent $manifest) 'ai-code-analysis.json') -Force
          '''
        }
        archiveArtifacts artifacts: '_jenkins/build-checkout-commit.txt,_jenkins/ai-code-analysis.json,TestResults/**', allowEmptyArchive: false, fingerprint: true
      }
    }

    stage('Publish') {
      steps {
        bat '''
        if exist "%WORKSPACE%\\%PUBLISH_DIR%" rmdir /s /q "%WORKSPACE%\\%PUBLISH_DIR%"
        dotnet publish "%API_PROJECT_PATH%" -c Release --no-restore -o "%WORKSPACE%\\%PUBLISH_DIR%"
        if errorlevel 1 exit /b %errorlevel%
        '''
        powershell '''
        $ErrorActionPreference = 'Stop'

        $publishDir = Join-Path $env:WORKSPACE $env:PUBLISH_DIR
        $commitFile = Join-Path $env:WORKSPACE $env:BUILD_CHECKOUT_COMMIT_FILE
        if (-not (Test-Path -LiteralPath $publishDir)) {
          throw "Publish folder not found: $publishDir"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $publishDir 'web.config'))) {
          throw "Publish artifact missing web.config: $publishDir"
        }
        if (-not (Test-Path -LiteralPath $commitFile)) {
          throw "Build checkout commit file missing before artifact validation: $commitFile"
        }

        $commit = (Get-Content -LiteralPath $commitFile -Raw).Trim()
        [ordered]@{
          source = 'Jenkins'
          repository = 'dennisfhxgit/SystemHealth'
          branch = 'master'
          commit = $commit
          buildSystem = 'Jenkins'
          jenkinsJob = $env:JOB_NAME
          jenkinsBuildNumber = $env:BUILD_NUMBER
          jenkinsBuildTag = $env:BUILD_TAG
          artifactName = "systemhealth-api-$($env:BUILD_NUMBER)"
          createdAtUtc = (Get-Date).ToUniversalTime().ToString('O')
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $publishDir 'artifact-manifest.json') -Encoding UTF8
        '''
        archiveArtifacts artifacts: '_jenkins/build-checkout-commit.txt,_jenkins/publish/SystemHealth.Api/artifact-manifest.json,build_provenance.env,SystemHealth-MSSQL-Removal-Evidence.md,SystemHealth-Test12-MyLifeStoryVault-Only-Evidence.md', allowEmptyArchive: false, fingerprint: true
      }
    }

    stage('Deploy to Test12') {
      steps {
        powershell '''
        $ErrorActionPreference = 'Stop'

        function Invoke-NativeChecked {
          param(
            [Parameter(Mandatory=$true)][string]$Label,
            [Parameter(Mandatory=$true)][string]$FilePath,
            [Parameter(Mandatory=$true)][string[]]$Arguments,
            [Parameter(Mandatory=$true)][int[]]$AcceptedExitCodes
          )

          $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -NoNewWindow -Wait -PassThru
          $exitCode = $process.ExitCode
          if ($AcceptedExitCodes -notcontains $exitCode) {
            throw "$Label failed with exit code $exitCode. Command: $FilePath $($Arguments -join ' ')"
          }

          Write-Host "$Label completed with accepted exit code $exitCode"
        }

        function Invoke-RobocopyChecked {
          param(
            [Parameter(Mandatory=$true)][string]$Label,
            [Parameter(Mandatory=$true)][string[]]$Arguments
          )

          Invoke-NativeChecked -Label "$Label robocopy" -FilePath 'robocopy.exe' -Arguments $Arguments -AcceptedExitCodes 0,1,2,3,4,5,6,7
        }

        function Grant-JenkinsLogReadAccess {
          param(
            [Parameter(Mandatory=$true)][string]$AppCmd,
            [Parameter(Mandatory=$true)][string]$AppPoolName,
            [Parameter(Mandatory=$true)][string]$BuildsRoot
          )

          if (-not (Test-Path -LiteralPath $BuildsRoot)) {
            throw "Jenkins builds root not found before ACL grant: $BuildsRoot"
          }

          $appPoolUser = (& $AppCmd list apppool $AppPoolName /text:processModel.userName).Trim()
          if ([string]::IsNullOrWhiteSpace($appPoolUser)) {
            throw "Could not resolve processModel.userName for app pool $AppPoolName."
          }

          Invoke-NativeChecked -Label "Grant Jenkins log read access to $appPoolUser" -FilePath 'icacls.exe' -Arguments @($BuildsRoot, '/grant', "$($appPoolUser):(OI)(CI)RX") -AcceptedExitCodes 0
        }

        function Remove-UnsafeRollbackSnapshotFiles {
          param([Parameter(Mandatory=$true)][string]$RollbackRoot)

          $reservedDeviceNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
          foreach ($name in @('CON', 'PRN', 'AUX', 'NUL')) {
            [void]$reservedDeviceNames.Add($name)
          }
          1..9 | ForEach-Object {
            [void]$reservedDeviceNames.Add("COM$_")
            [void]$reservedDeviceNames.Add("LPT$_")
          }

          $blockedExtensions = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
          foreach ($extension in @('.cer', '.crt', '.key', '.pem', '.pfx', '.p12')) {
            [void]$blockedExtensions.Add($extension)
          }

          $removed = 0
          Get-ChildItem -LiteralPath $RollbackRoot -Recurse -File -Force | ForEach-Object {
            $baseName = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
            if (-not $reservedDeviceNames.Contains($baseName) -and -not $blockedExtensions.Contains($_.Extension)) {
              return
            }

            $deletePath = if ($_.FullName.StartsWith('\\', [StringComparison]::Ordinal)) {
              ([string][char]92 + [string][char]92 + '?' + [string][char]92 + 'UNC' + [string][char]92) + $_.FullName.TrimStart('\')
            } else {
              ([string][char]92 + [string][char]92 + '?' + [string][char]92) + $_.FullName
            }

            [System.IO.File]::Delete($deletePath)
            $removed++
            Write-Host "Removed unsafe rollback snapshot file: $($_.FullName)"
          }

          Write-Host "Rollback snapshot unsafe file cleanup removed $removed file(s)."
        }

        $source = Join-Path $env:WORKSPACE $env:PUBLISH_DIR
        $target = $env:TEST12_PATH
        $appPool = $env:TEST12_APPPOOL
        $appOffline = Join-Path $target 'app_offline.htm'
        $appcmd = Join-Path $env:windir 'System32/inetsrv/appcmd.exe'
        $jenkinsBuildsRoot = 'C:/ProgramData/Jenkins/.jenkins/jobs/SystemHealth/builds'
        $commitFile = Join-Path $env:WORKSPACE $env:BUILD_CHECKOUT_COMMIT_FILE
        $manifestPath = Join-Path $source 'artifact-manifest.json'

        if (-not (Test-Path -LiteralPath $source)) {
          throw "Publish folder not found before Test12 deployment: $source"
        }
        if (-not (Test-Path -LiteralPath $target)) {
          throw "Test12 target folder not found: $target"
        }
        if (-not (Test-Path -LiteralPath $commitFile)) {
          throw "Build checkout commit file missing before Test12 deployment: $commitFile"
        }
        if (-not (Test-Path -LiteralPath $manifestPath)) {
          throw "Deploy artifact manifest missing before Test12 deployment: $manifestPath"
        }

        $commit = (Get-Content -LiteralPath $commitFile -Raw).Trim()
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        git fetch origin '+refs/heads/master:refs/remotes/origin/master' --prune
        $branchTip = (git rev-parse 'origin/master').Trim()
        if ([string]$commit -ne [string]$branchTip) {
          throw "Jenkins checkout is stale. stage=Deploy to Test12, checkout=$commit, origin/master=$branchTip. Aborting before IIS changes."
        }
        if ([string]$manifest.commit -ne [string]$commit) {
          throw "Deploy artifact commit mismatch. checkout=$commit, manifest=$($manifest.commit). Aborting before IIS changes."
        }
        if ([string]$manifest.repository -ne 'dennisfhxgit/SystemHealth') {
          throw "Deploy artifact repository mismatch. expected=dennisfhxgit/SystemHealth, manifest=$($manifest.repository)."
        }
        if ([string]$manifest.branch -ne 'master') {
          throw "Deploy artifact branch mismatch. expected=master, manifest=$($manifest.branch)."
        }

        try {
          $rollbackRoot = Join-Path $env:WORKSPACE "_rollback/$env:BUILD_NUMBER/website-current"
          if (Test-Path -LiteralPath $rollbackRoot) {
            Remove-Item -LiteralPath $rollbackRoot -Recurse -Force
          }

          New-Item -ItemType Directory -Force -Path $rollbackRoot | Out-Null
          $rollbackArgs = @(
            $target,
            $rollbackRoot,
            '/E',
            '/R:3',
            '/W:5',
            '/NFL',
            '/NDL',
            '/NJH',
            '/NJS',
            '/NP',
            '/XF',
            'appsettings.json',
            'appsettings.Development.json',
            'appsettings.Production.json',
            'appsettings.*.json',
            '*.cer',
            '*.crt',
            '*.key',
            '*.pem',
            '*.pfx',
            '*.p12',
            'AUX.*',
            'CON.*',
            'NUL.*',
            'PRN.*',
            'COM?.*',
            'LPT?.*'
          )
          Invoke-RobocopyChecked -Label 'SystemHealth Test12 rollback snapshot' -Arguments $rollbackArgs
          Remove-UnsafeRollbackSnapshotFiles -RollbackRoot $rollbackRoot

          '<html><body>Deployment in progress.</body></html>' | Set-Content -LiteralPath $appOffline -Encoding UTF8

          Invoke-NativeChecked -Label "Stop app pool $appPool" -FilePath $appcmd -Arguments @('stop', 'apppool', "/apppool.name:$appPool") -AcceptedExitCodes 0
          Start-Sleep -Seconds 2

          $assetTargets = @(
            (Join-Path $target 'assets'),
            (Join-Path $target 'wwwroot/assets')
          )
          foreach ($assetTarget in $assetTargets) {
            if (Test-Path -LiteralPath $assetTarget) {
              Remove-Item -LiteralPath $assetTarget -Recurse -Force
            }
          }

          $robocopyArgs = @(
            $source,
            $target,
            '/E',
            '/R:3',
            '/W:5',
            '/NFL',
            '/NDL',
            '/NJH',
            '/NJS',
            '/NP',
            '/XF',
            'appsettings.Development.json',
            'appsettings.Production.json',
            'appsettings.*.json'
          )

          Invoke-RobocopyChecked -Label 'SystemHealth Test12' -Arguments $robocopyArgs
          Grant-JenkinsLogReadAccess -AppCmd $appcmd -AppPoolName $appPool -BuildsRoot $jenkinsBuildsRoot
        }
        finally {
          if (Test-Path -LiteralPath $appOffline) {
            Remove-Item -LiteralPath $appOffline -Force
          }

          Invoke-NativeChecked -Label "Start app pool $appPool" -FilePath $appcmd -Arguments @('start', 'apppool', "/apppool.name:$appPool") -AcceptedExitCodes 0
        }
        '''
        archiveArtifacts artifacts: '_rollback/**', allowEmptyArchive: false
      }
    }

    stage('Test12 Smoke') {
      steps {
        powershell '''
        $ErrorActionPreference = 'Stop'

        $checks = @(
          '/',
          '/health',
          '/api/system-health/code-quality-security',
          '/api/system-health/system-alerts',
          '/api/system-health/admin-environment',
          '/api/system-health/email-workers',
          '/api/system-health/critical-events',
          '/api/system-health/backups',
          '/api/system-health/jenkins-log',
          '/api/system-health/test-results',
          '/api/system-health/artifact-history',
          '/api/system-health/ai-code-analysis',
          '/system-health',
          '/system-health/code-quality-security'
        )

        foreach ($path in $checks) {
          $uri = "$env:TEST12_URL$path"
          $response = Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 30
          if ($response.StatusCode -ne 200) {
            throw "$uri returned HTTP $($response.StatusCode)"
          }

          if ($path -eq '/' -or $path.StartsWith('/system-health')) {
            if (-not $response.Content.Contains('id="app"')) {
              throw "$uri did not return the Vue app root."
            }
          }

          Write-Host "$uri returned HTTP 200"
        }

        & (Join-Path $env:WORKSPACE 'scripts/ci/Write-SystemHealthApiPerformanceResults.ps1') `
          -BaseUrl $env:TEST12_URL `
          -OutputPath (Join-Path $env:WORKSPACE 'TestResults/api-performance.junit.xml') `
          -SamplesPerEndpoint 3

        $appcmd = Join-Path $env:windir 'System32/inetsrv/appcmd.exe'
        $appPoolUser = (& $appcmd list apppool $env:TEST12_APPPOOL /text:processModel.userName).Trim()
        if ([string]::IsNullOrWhiteSpace($appPoolUser)) {
          throw "Could not resolve processModel.userName for app pool $env:TEST12_APPPOOL."
        }

        $testResultsPath = Join-Path $env:WORKSPACE 'TestResults'
        $grantResult = Start-Process -FilePath 'icacls.exe' -ArgumentList @($testResultsPath, '/grant', "$($appPoolUser):(OI)(CI)RX") -NoNewWindow -Wait -PassThru
        if ($grantResult.ExitCode -ne 0) {
          throw "Grant TestResults read access to $appPoolUser failed with exit code $($grantResult.ExitCode)."
        }
        '''
        junit allowEmptyResults: false, testResults: 'TestResults/api-performance.junit.xml'
        archiveArtifacts artifacts: 'TestResults/api-performance.junit.xml', allowEmptyArchive: false, fingerprint: true
      }
    }
  }

  post {
    success {
      echo 'SystemHealth pipeline completed successfully and deployed to Test12.'
    }
    failure {
      echo 'SystemHealth pipeline failed. Test12 deployment may not have run if an earlier stage failed.'
    }
  }
}
