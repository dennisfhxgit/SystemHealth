# SystemHealth Test12 MyLifeStoryVault-Only Evidence

## Files Copied Or Created

- Copied the Vue System Health module from CRM-Test-Two.
- Created a standalone Vue/Vite app shell.
- Created a standalone .NET 10 API project.
- Created source configuration for `MyLifeStoryVault-Ltd/My-Life-Story-Vault`.

## Dependency Map

- Frontend entry: `src/App.vue`
- Extracted module: `src/modules/system/System Health`
- Critical-state helper: `src/modules/system/systemHealthCriticalState.ts`
- API project: `SystemHealth.Api`
- API source: `SystemHealth.Api/Program.cs`
- Configuration: `SystemHealth.Api/appsettings.json`

Retained endpoint patterns:

- `/api/system-health/code-quality-security`
- `/api/system-health/backups`
- `/api/system-health/system-alerts`
- `/api/system-health/admin-environment`
- `/api/system-health/email-workers`
- `/api/system-health/critical-events`
- `/api/system-health/jenkins-log`
- `/api/system-health/test-results`
- `/api/system-health/artifact-history`
- `/api/system-health/ai-code-analysis`

## Repository References Found Before Cleanup

Old source references documented from the task and source scan:

- `dennisfhxgit`
- `MyLifeStoryVaultTest`
- `https://github.com/dennisfhxgit/MyLifeStoryVaultTest`

## Non-MyLifeStoryVault References Removed

- Removed copied CRM/FHX-oriented tests from the extraction target.
- Replaced copied default application filters with `my-life-story-vault`.
- Removed source configuration paths that pointed at the old test workspace.

## Remaining Reporting Scope

Only this repository is configured in source:

```text
MyLifeStoryVault-Ltd/My-Life-Story-Vault
```

## Test12 Configuration Notes

- Test12 base route: `/`
- API base URL: `/api`
- GitHub repository source: `MyLifeStoryVault-Ltd/My-Life-Story-Vault`
- Jenkins job/source: `SystemHealth`
- Sonar project/source: `MyLifeStoryVault-Ltd_My-Life-Story-Vault`
- Runtime secrets required: provider credentials for GitHub, Jenkins, and SonarQube if live provider data is enabled. Secret values are not stored in source.

## Code Quality & Security Wiring

- `/api/system-health/code-quality-security` now uses the CRM-Test-Two hardened `CodeQualitySecurityService`, `CodeQualityGitHubSecurityClient`, `CodeQualitySonarClient`, and DTO contract.
- The standalone adapter maps only `my-life-story-vault` to `MyLifeStoryVault-Ltd/My-Life-Story-Vault` with Sonar project `MyLifeStoryVault-Ltd_My-Life-Story-Vault`.
- The endpoint reads live SonarQube data when `SystemHealth__SonarQube__BaseUrl`, `SystemHealth__SonarQube__ProjectKey`, and `SystemHealth__SonarQube__Token` are configured at runtime.
- The endpoint reads GitHub Dependabot, CodeQL freshness/alerts, and Secret Scanning alerts for `MyLifeStoryVault-Ltd/My-Life-Story-Vault` when `SystemHealth__Repository__GitHubToken` is configured at runtime.
- The endpoint uses the CRM hardened artifact discovery/trust logic for Lint & Standards, OWASP Dependency-Check, CycloneDX SBOM, and Playwright rather than returning placeholder rows.
- The `SystemHealth` deployment Jenkinsfile does not run MyLifeStoryVault mobile/backend CI during deployment. Code Quality & Security is served by the CRM hardened endpoint/client logic and provider/artifact discovery, without blocking the SystemHealth deploy path on another repository's coverage run.
- The optional artifact producer is `scripts/ci/Write-MyLifeStoryVaultCodeQualityArtifacts.ps1`; it can write `lint-report.json`, a CycloneDX SBOM, and OWASP Dependency-Check reports for `My-Life-Story-Vault` when run separately from the deployment path.
- The optional artifact producer has hard command timeouts: restore commands default to 10 minutes, normal lint/test/build commands default to 5 minutes, and Dependency-Check defaults to 15 minutes. A timeout is written into `lint-report.json` as a failing finding instead of leaving the producer stuck without artifacts.
- Missing provider credentials or missing artifact paths are reported as `Unavailable`; they are not treated as healthy zero-finding results.
- The old `MyLifeStoryVaultTest` Jenkins job that points at `dennisfhxgit/MyLifeStoryVaultTest` is not used. Code Quality & Security for this standalone app is scoped to `MyLifeStoryVault-Ltd/My-Life-Story-Vault` through configuration and provider lookups.

## Verification Results

- Dependency install: `npm install` passed; npm audit reported 0 vulnerabilities.
- TypeScript check: `npm run typecheck` passed.
- Lint/typecheck: `npm run lint` passed.
- Frontend test command and result: `npm run test` passed, 1 file and 2 tests.
- Frontend build command and result: `npm run build` passed.
- Backend build command and result: `dotnet build SystemHealth.sln -c Release` passed with 0 warnings and 0 errors.
- Backend test command and result: `dotnet test SystemHealth.sln -c Release --no-build` exited successfully; no backend test project exists yet.
- Publish command and result: `dotnet publish SystemHealth.Api\SystemHealth.Api.csproj -c Release -o _publish` passed after rerunning serially. The first parallel build/publish attempt hit a transient static asset compression file lock.
- Code Quality & Security producer syntax check: PowerShell parser validation passed for `scripts/ci/Write-MyLifeStoryVaultCodeQualityArtifacts.ps1`.
- Old vault reference scan: no configured source references to `MyLifeStoryVaultTest` or `dennisfhxgit/MyLifeStoryVaultTest` remain outside this evidence file.
- Code Quality & Security smoke-test result: local published app on `http://127.0.0.1:5012` returned HTTP 200 for `/api/system-health/code-quality-security?applicationKey=my-life-story-vault&environment=Development`, reported selected application `my-life-story-vault`, GitHub repository `My-Life-Story-Vault`, Sonar component `MyLifeStoryVault-Ltd_My-Life-Story-Vault`, and provider statuses for SonarQube, GitHub Dependabot, GitHub CodeQL, GitHub Secret Scanning, Lint & Standards, OWASP Dependency-Check, CycloneDX SBOM, and Playwright.
- Endpoint smoke-test results: local published app on `http://127.0.0.1:5012` returned HTTP 200 for `/health`, `/`, `/api/system-health/code-quality-security`, `/api/system-health/system-alerts`, `/api/system-health/admin-environment`, `/api/system-health/email-workers`, `/api/system-health/critical-events`, `/api/system-health/backups`, `/api/system-health/jenkins-log`, `/api/system-health/test-results`, `/api/system-health/artifact-history`, and `/api/system-health/ai-code-analysis`.
- Refresh/deep-link routing: local published app returned HTTP 200 and the Vue root for `/system-health`, `/system-health/code-quality-security`, and `/anything/deep/link`.
- Deployment notes: source includes a Jenkins `SystemHealth` pipeline that checks out `dennisfhxgit/SystemHealth` `master`, runs frontend install/typecheck/lint/test/build, runs backend restore/build/test/publish, deploys the publish artifact to `W:\vhosts\fhx.co.nz\test12.fhx.co.nz`, restarts app pool `test12.fhx.co.nz(domain)(4.0)(pool)`, and runs Test12 endpoint/deep-link smoke checks. The live Jenkins `SystemHealth` job config was changed from an empty flow definition to an SCM pipeline that loads this repo `Jenkinsfile`; the previous config was backed up under `C:\Users\mldconst\Desktop\Handovers\JenkinsConfigBackups`.
- Test12 route load: not run because the app has not been deployed to a live Test12 target in this task.

## Scope Boundary

Test10 and MLSCTest10 were not modified.

## Blockers

- None in source.
