# SystemHealth Agent Guide

## Purpose

SystemHealth is a standalone, read-only health dashboard deployed to `https://test12.fhx.co.nz`.

The app is intentionally not a CRM shell. It has no login flow, no MSSQL session state, no user persistence, and no writable critical-alert history. It renders the System Health experience directly at `/` and serves API data from `/api/system-health/*`.

The repository is built around two parts:

- Vue 3 + Vite frontend under `src/`.
- ASP.NET Core API and static file host under `SystemHealth.Api/`.

The Jenkins job deploys the published ASP.NET Core app to:

```text
W:/vhosts/fhx.co.nz/test12.fhx.co.nz
```

## Repo Layout

```text
.
├─ index.html                         # Vite HTML entry; browser title lives here
├─ package.json                       # frontend commands and dependencies
├─ vite.config.ts                     # Vite, Vitest, coverage, and dev proxy config
├─ Jenkinsfile                        # governed CI/CD pipeline for master -> Test12
├─ README.md                          # short project summary
├─ docs/                              # repo notes for agents
├─ scripts/ci/                        # Jenkins artifact writers and test result helpers
├─ src/                               # Vue frontend
│  ├─ App.vue                         # shell that mounts the System Health module
│  ├─ App.test.ts                     # frontend integration-style tests with mocked API responses
│  └─ modules/system/
│     ├─ systemHealthCriticalState.ts # critical-state reporting helpers
│     └─ System Health/
│        ├─ Index.vue                 # top-level tab/menu orchestration
│        ├─ shared.ts                 # endpoint/fetch/status/date/filter helpers
│        ├─ shared-panel.css          # common panel/table/card styling
│        └─ <section>/Index.vue       # one page per health section
└─ SystemHealth.Api/
   ├─ Program.cs                      # DI, endpoint map, static file hosting, config DTOs
   ├─ appsettings.json                # non-secret runtime paths/options
   ├─ *.Reader.cs                     # runtime providers for Jenkins/Sonar/Test12 data
   ├─ Standalone*.cs                  # standalone adapters for CRM/Test12 health pages
   └─ SystemHealth/                   # copied CRM code quality service namespace
```

Generated local/Jenkins output folders are not source:

```text
.sonarqube/
coverage/
dist/
TestResults/
_jenkins/
_rollback/
node_modules/
SystemHealth.Api/bin/
SystemHealth.Api/obj/
```

Do not stage generated output unless the user explicitly requests it and there is a clear reason.

## Frontend Architecture

The browser loads `index.html`, then `src/main.ts`, then `src/App.vue`.

`App.vue` mounts the System Health module directly. There is no router and no login chrome.

The main System Health menu is in:

```text
src/modules/system/System Health/Index.vue
```

Current section keys are defined in `shared.ts`:

```text
codeQuality
jenkinsLogs
testResults
aiCodeAnalysis
systemAlerts
artifactHistory
adminEnvironment
emailWorkers
```

Each child page follows the same pattern:

1. Import helpers from `shared.ts`.
2. Call `useHealthChildPage` or `useFilteredHealthChildPage`.
3. Render the returned JSON defensively.
4. Emit `stateChange`, `loadingChange`, and `errorChange` so the parent can display loading/error state and mark critical tabs.

Use `useFilteredHealthChildPage` for pages with application/environment filters, such as Jenkins Logs, Test Results, and Artifact History. Use `useHealthChildPage` for direct endpoints such as System Alerts, Admin & Environment, and Email Workers.

### Frontend API Calls

The shared endpoint helper always appends a timestamp cache buster:

```ts
endpoint('/api/system-health/email-workers')
```

Frontend fetch errors are intentionally generic in the UI. If a page says "Unable to load ...", check the API endpoint directly and then the Windows Application log if running under IIS.

### Browser Tab Title

The browser tab title is in:

```text
index.html
```

Current title:

```html
<title>System Health - Forge CRM</title>
```

## Backend Architecture

`SystemHealth.Api/Program.cs` does the backend wiring:

- Binds `SystemHealth` config from `appsettings.json`.
- Registers all readers/services in DI.
- Hosts static files from the Vite build output when published.
- Maps `/api/system-health/*` endpoints.
- Maps `/health`.
- Falls back to `index.html` for frontend paths.

Important endpoints:

```text
GET  /health
GET  /api/system-health/code-quality-security
GET  /api/system-health/jenkins-log
GET  /api/system-health/test-results
GET  /api/system-health/ai-code-analysis
GET  /api/system-health/system-alerts
GET  /api/system-health/admin-environment
GET  /api/system-health/email-workers
GET  /api/system-health/artifact-history
GET  /api/system-health/backups
GET  /api/system-health/critical-events
POST /api/system-health/critical-events
GET  /api/system-health/critical-events/history
POST /api/system-health/critical-events/history/{id}/acknowledge
```

Critical events and backups are intentionally non-persistent/unavailable in this standalone app.

## Runtime Data Providers

### Code Quality & Security

Files:

```text
SystemHealth.Api/StandaloneCodeQualitySecurityEndpoint.cs
SystemHealth.Api/SystemHealth/CodeQualitySecurityService.cs
scripts/ci/Write-SystemHealthCodeQualityArtifacts.ps1
scripts/ci/Write-AiCodeAnalysisArtifact.ps1
```

The Jenkins pipeline writes code quality artifacts to:

```text
C:/ProgramData/Jenkins/.jenkins/fhx-system-health/SystemHealth/latest
```

The API reads those artifacts and returns the Code Quality & Security and AI Code Analysis data. Avoid ad hoc parsing when a script already writes a structured artifact.

### Jenkins Logs, Test Results, Artifact History, AI Code Analysis

Files:

```text
SystemHealth.Api/JenkinsLogReader.cs
SystemHealth.Api/JenkinsTestResultsReader.cs
SystemHealth.Api/JenkinsArtifactHistoryReader.cs
SystemHealth.Api/JenkinsAiCodeAnalysisReader.cs
```

These readers use Jenkins/Sonar/artifact files and runtime config. They are read-only. The deployment grants the Test12 app pool read access to Jenkins build logs and TestResults where needed.

### System Alerts

File:

```text
SystemHealth.Api/StandaloneSystemAlertsReader.cs
```

Configured by:

```json
"SystemAlerts": {
  "ApplicationServerDriveLetters": [ "B:\\", "C:\\", "W:\\" ],
  "DataServerDriveLetters": [ "C:\\", "D:\\", "L:\\" ],
  "ApplicationServerMetricsSnapshotPath": "C:\\ProgramData\\FHX\\SystemHealth\\test11-application-server-metrics.json",
  "DataServerMetricsUrl": "http://remotesql.fhx.co.nz/api/v1/system/metrics"
}
```

This page combines application server checks, data server checks, drive metrics, and critical alert display. It should never assume only one server or a fixed number of drives; use the configured drive lists and remote data shape.

### Admin & Environment

Files:

```text
SystemHealth.Api/AdminEnvironmentHealthService.cs
SystemHealth.Api/AdminEnvironmentDtos.cs
SystemHealth.Api/AdminEnvironmentUptimeProvider.cs
```

Configured by:

```json
"AdminEnvironment": {
  "Targets": [
    {
      "Name": "Test12",
      "Url": "https://test12.fhx.co.nz/health",
      "UptimeAppPoolName": "test12.fhx.co.nz(domain)(4.0)(pool)"
    }
  ]
}
```

This checks the configured health URL and, on Windows/IIS, uses the app pool name for uptime details.

### Email Workers

File:

```text
SystemHealth.Api/StandaloneEmailWorkersReader.cs
```

The frontend page was already compatible with the CRM-Test-Two health shape. The standalone reader loads CRM runtime state and returns the same worker shape for:

```text
system-email-queue
email-trigger-queue
mailchimp-subscription-sync
```

The reader uses:

- CRM runtime secrets JSON for the CRM connection string and Mailchimp worker config.
- SQL tables such as `ProcessedEmail`, `EmailTriggerQueue`, `EmailUnsubscribes`, `EmailWorkerRunState`, and `EmailDeliveryDefaults` when present.
- Schema checks before reading optional columns/tables.
- A warning payload instead of throwing when secrets/SQL cannot be read.

Runtime secret path:

```text
C:\ProgramData\FHX\CRM\secrets\test11.fhx.co.nz(domain)(4.0)(pool)\secrets.json
```

The Jenkins deploy stage must grant the Test12 app pool read access to both:

- the secrets directory
- the `secrets.json` file

The existing file may have explicit ACLs and may not inherit the directory ACL. If Test12 returns HTTP 500 for `/api/system-health/email-workers`, check the Windows Application log for `UnauthorizedAccessException` against `secrets.json`.

## Configuration Rules

Non-secret values live in:

```text
SystemHealth.Api/appsettings.json
```

Secrets must not be committed. Keep these blank in source:

```json
"GitHubToken": "",
"ApiToken": "",
"Token": ""
```

Jenkins credentials are injected in the pipeline, for example:

```text
sonar-token
```

Runtime secrets are external files under `C:\ProgramData\FHX\...`.

## Jenkins Pipeline

The pipeline is in `Jenkinsfile` and is governed around `master`.

Major stages:

1. `Checkout Repo`
2. `Provenance`
3. `Install Dependencies`
4. `Frontend Build And Tests`
5. `Backend Build And Tests`
6. `Code Quality Artifact Publish`
7. `Publish`
8. `Deploy to Test12`
9. `Test12 Smoke`

Important pipeline behavior:

- `disableConcurrentBuilds()` prevents overlapping deployments.
- `skipDefaultCheckout(true)` and explicit checkout keep branch provenance controlled.
- `Provenance` aborts if the checkout commit is not exactly `origin/master`.
- `Backend Build And Tests` runs Sonar with `sonar.qualitygate.wait=true`.
- `Deploy to Test12` validates artifact manifest commit/repo/branch before IIS changes.
- Deployment creates `_rollback/<build>/website-current`.
- Deployment writes `app_offline.htm`, stops the app pool, robocopies publish output, grants ACLs, removes `app_offline.htm`, and restarts the app pool.
- `Test12 Smoke` calls all important endpoints and selected frontend routes.

### Jenkins ACL Grants

Deploy grants read access to:

- Jenkins build logs root
- CRM runtime secrets directory
- CRM runtime `secrets.json`

Test12 smoke also grants app pool read access to `TestResults` before publishing the API performance JUnit result.

If an endpoint works locally but fails after deploy, check whether the deployed app pool can read the file, folder, or network resource used by that endpoint.

## Verification Commands

Run these from the repo root:

```powershell
npm ci
npm run typecheck
npm run test:coverage
npm run build
dotnet build .\SystemHealth.sln -c Release
dotnet test .\SystemHealth.sln -c Release
dotnet publish .\SystemHealth.Api\SystemHealth.Api.csproj -c Release -o .\_publish
```

For a backend-only change, at minimum run:

```powershell
dotnet build .\SystemHealth.Api\SystemHealth.Api.csproj
```

For a frontend-only change, at minimum run:

```powershell
npm run build
```

For changes that touch rendered page behavior, run:

```powershell
npm run test:coverage
npm run build
```

### Local API Smoke

Example:

```powershell
dotnet run --project .\SystemHealth.Api\SystemHealth.Api.csproj --urls http://127.0.0.1:5130
Invoke-WebRequest -Uri http://127.0.0.1:5130/api/system-health/email-workers -UseBasicParsing
```

The Vite dev server proxies `/api` to `http://localhost:5012`; if you use Vite locally, run the API on that port or update the proxy.

### Test12 Smoke Checks

Useful direct checks:

```powershell
Invoke-WebRequest -Uri https://test12.fhx.co.nz/health -UseBasicParsing
Invoke-WebRequest -Uri https://test12.fhx.co.nz/api/system-health/code-quality-security -UseBasicParsing
Invoke-WebRequest -Uri https://test12.fhx.co.nz/api/system-health/email-workers -UseBasicParsing
Invoke-WebRequest -Uri https://test12.fhx.co.nz/api/system-health/artifact-history -UseBasicParsing
```

If a request returns 500 under IIS, inspect the Windows Application log:

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-30)} |
  Where-Object { $_.ProviderName -like '*.NET Runtime*' -or $_.Message -like '*SystemHealth*' -or $_.Message -like '*test12*' } |
  Select-Object -First 20 TimeCreated,ProviderName,Id,LevelDisplayName,Message |
  Format-List
```

## Sonar Notes

The quality gate fails on any new violation. Local `dotnet build` can pass while Jenkins fails in Sonar.

Common rules encountered in this repo:

- `S1192`: repeated string literals. Use constants for repeated labels/aliases.
- `S1144`: unused private members.
- `S3903`: classes outside named namespaces. Many existing classes are currently grandfathered; new code should prefer `namespace SystemHealth.Api;` or another named namespace.
- Cognitive complexity and parameter count rules can fail new or changed code.

The Jenkinsfile currently excludes large standalone reader files from coverage, but not from issue analysis. Treat Sonar warnings in Jenkins logs as actionable if they are on new code.

## Adding Or Modifying Pages

When adding a new System Health page:

1. Add a new section key to `SectionKey` in `shared.ts`.
2. Create `src/modules/system/System Health/<PageName>/Index.vue`.
3. Register it in `System Health/Index.vue`.
4. Add an API endpoint in `Program.cs`.
5. Add or extend a reader/service in `SystemHealth.Api/`.
6. Add frontend test coverage in `src/App.test.ts`.
7. Add endpoint smoke coverage in Jenkins if it must be deployment-gated.

Keep payload shapes stable and defensive. The frontend should tolerate missing optional fields, and the backend should return structured warning/unavailable payloads rather than throwing raw exceptions for missing runtime resources.

## Safety Rules For Future Agents

- Do not commit secrets, tokens, copied credential files, generated coverage, or Jenkins output folders.
- Do not remove rollback hardening or deployment provenance checks in `Jenkinsfile`.
- Do not assume the deployed IIS app pool has the same access as the interactive user.
- Do not update Test12 appsettings manually as the long-term fix; put deploy/runtime access fixes in source.
- Do not change app pool names, target paths, or runtime secret paths without verifying the corresponding IIS/Jenkins configuration.
- Do not hand-roll data parsing when the repo already has a structured reader or artifact writer.
- Prefer copying proven hardened logic from CRM-Test-Two when wiring CRM-derived System Health pages.
- If a build fails, inspect the Jenkins build log and, for deployed 500s, the Windows Application log before changing code.

## Current Known Runtime Locations

```text
Repo workspace:
C:\ProgramData\Jenkins\.jenkins\workspace\SystemHealth

Jenkins job builds:
C:\ProgramData\Jenkins\.jenkins\jobs\SystemHealth\builds

Code quality artifact root:
C:\ProgramData\Jenkins\.jenkins\fhx-system-health\SystemHealth\latest

Test12 deploy target:
W:\vhosts\fhx.co.nz\test12.fhx.co.nz

CRM runtime secrets:
C:\ProgramData\FHX\CRM\secrets\test11.fhx.co.nz(domain)(4.0)(pool)\secrets.json

Application server metrics snapshot:
C:\ProgramData\FHX\SystemHealth\test11-application-server-metrics.json
```

