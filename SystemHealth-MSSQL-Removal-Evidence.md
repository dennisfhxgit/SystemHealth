# SystemHealth MSSQL Removal Evidence

## References Found Before Removal

The source extraction review found the CRM module depended on SQL-backed areas that are intentionally not present in this standalone app:

- `ConnectionStrings:CRM`
- SQL persistence repositories for critical alert history and acknowledgement state
- SQL-backed email worker run state and queue state
- login/authentication routes and database-backed login services
- critical alert history and acknowledgement endpoints backed by persistence

## References Removed

- No `ConnectionStrings:CRM` setting exists in the standalone app.
- No SQL client, Entity Framework, `DbContext`, or `UseSqlServer` registration exists.
- No login, logout, authentication, authorization, cookie, or session middleware exists.
- Critical alert POST/GET uses in-memory request handling only and does not persist history.
- Email Workers returns an explicit unavailable read-only response because the worker state was database-backed.

## References Retained

- `MSSQL` appears only in user-facing status/evidence text explaining that the dependency is intentionally absent.
- The API route names are retained so the extracted Vue module can load without redesign.

## Runtime Confirmations

- `ConnectionStrings:CRM` is not required at runtime.
- No endpoint requires MSSQL.
- Login is not required.
- Users land on `/`, which renders the System Health page directly.

## Commands And Results

- Dependency install: `npm install` passed; npm audit reported 0 vulnerabilities.
- TypeScript check: `npm run typecheck` passed.
- Lint/typecheck: `npm run lint` passed.
- Frontend tests: `npm run test` passed, 1 file and 2 tests.
- Frontend build: `npm run build` passed.
- Backend build: `dotnet build SystemHealth.sln -c Release` passed with 0 warnings and 0 errors.
- Backend tests: `dotnet test SystemHealth.sln -c Release --no-build` exited successfully; no backend test project exists yet.
- Publish: `dotnet publish SystemHealth.Api\SystemHealth.Api.csproj -c Release -o _publish` passed. An initial parallel build/publish attempt failed due an `obj` compression file lock; serial publish passed without source changes.
- Endpoint smoke tests: local published app on `http://127.0.0.1:5012` returned HTTP 200 for `/health`, `/`, `/api/system-health/code-quality-security`, `/api/system-health/system-alerts`, `/api/system-health/admin-environment`, `/api/system-health/email-workers`, `/api/system-health/critical-events`, `/api/system-health/backups`, `/api/system-health/jenkins-log`, `/api/system-health/test-results`, `/api/system-health/artifact-history`, and `/api/system-health/ai-code-analysis`.
- Refresh/deep-link checks: local published app returned HTTP 200 and the Vue root for `/system-health`, `/system-health/code-quality-security`, and `/anything/deep/link`.
- Test12 browser load/deep-link checks: not run because the app has not been deployed to a live Test12 target in this task.

## Blockers

- Test12 deployment and live browser verification require the Jenkins `SystemHealth` job to run. The job is now wired to the repo Jenkinsfile, but Jenkins API access from this session returned HTTP 403, so the job was not triggered from Codex.
