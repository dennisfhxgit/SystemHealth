# SystemHealth Test12

Standalone read-only System Health dashboard for `MyLifeStoryVault-Ltd/My-Life-Story-Vault`.

The app has no MSSQL requirement, no login route, no saved credentials, no database-backed session state, and no critical alert or acknowledgement persistence. External provider credentials and artifact paths are runtime configuration only.

## Commands

```powershell
npm ci
npm run build
npm run test
dotnet build .\SystemHealth.sln -c Release
dotnet publish .\SystemHealth.Api\SystemHealth.Api.csproj -c Release -o .\_publish
```

## Runtime Configuration

Configure non-secret values under `SystemHealth` in `SystemHealth.Api/appsettings.json` or environment variables. Secrets such as GitHub, Jenkins, and SonarQube credentials must remain outside source.

## Agent Notes

Detailed repo-orientation notes are in [`docs/agent-guide.md`](docs/agent-guide.md).
