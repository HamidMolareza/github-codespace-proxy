# Operations Guide

## Local Control Plane

Run the API on the workstation:

```bash
dotnet run --project src/GhProxy.Api/GhProxy.Api.csproj --urls http://127.0.0.1:5080
```

Run the frontend:

```bash
cd frontend
npm install
npm run dev
```

The frontend proxies `/api/*` to `http://127.0.0.1:5080`.

## Docker Compose

The repository includes `compose.yml` for running both services:

- `backend`: ASP.NET Core API on container port `8080`, published as `127.0.0.1:5080`.
- `frontend`: Node serving the built React app on container port `8080`, published as `127.0.0.1:5173`.
- `gh-proxy-data`: named volume for SQLite, JSONL logs, and Data Protection state.

Start:

```bash
docker compose up --build -d
```

Open:

```text
http://127.0.0.1:5173
```

Smoke test:

```bash
docker compose ps
curl http://127.0.0.1:5080/api/health
curl http://127.0.0.1:5080/api/activity/summary
curl http://127.0.0.1:5080/api/diagnostics/runtime
```

Stop:

```bash
docker compose down
```

Remove persisted app data only when you intentionally want a clean database:

```bash
docker compose down -v
```

## GitHub Account Setup

1. Create a GitHub PAT with Codespaces permissions. Classic PATs need the `codespace` scope. Billing usage may require additional account/plan access and can be unavailable for some tokens.
2. Open the dashboard and add the GitHub username plus PAT.
3. Click validate to confirm the token maps to the expected authenticated user.
4. Click sync to load Codespaces.
5. Use create/start/stop/export/delete actions from the Codespaces table.

PAT values are encrypted at rest and are not displayed after save.

## Idle Auto-Stop

The `GitHubCodespaceMaintenanceService` periodically syncs account Codespaces and stops running Codespaces that have been idle longer than `GitHub:AutoStopIdleMinutes`.

Configuration lives under `GitHub` in `src/GhProxy.Api/appsettings.json`:

```json
"GitHub": {
  "ApiBaseUrl": "https://api.github.com/",
  "ApiVersion": "2026-03-10",
  "SyncIntervalSeconds": 300,
  "AutoStopIdleMinutes": 30,
  "RequestTimeoutSeconds": 30
}
```

The worker only stops idle Codespaces. It does not delete Codespaces automatically and does not start another account when quota is low.

## Observability

The Activity tab shows recent operational events, GitHub API failures, runtime diagnostics, and redacted output snippets. Use it first when token validation, sync, usage, or lifecycle actions do not behave as expected.

Each API request receives an `X-Correlation-ID` response header. If an API call fails, copy the correlation ID from the UI or browser network panel and filter Activity by that value.

The backend stores events in SQLite table `OperationalEvents` and writes local JSONL files when enabled:

```json
"Observability": {
  "LogDirectory": "data/logs",
  "RetentionDays": 14,
  "MaxOutputChars": 4000,
  "EnableJsonlFile": true
}
```

JSONL retention only deletes files matching `operational-*.jsonl` after `RetentionDays`. The retention worker does not delete the SQLite event table.

Secrets are redacted before command output, command display strings, details JSON, and error messages are persisted.

## Git Commands In This Workspace

The sandbox has a read-only `.git` mount, so this implementation stores repository metadata under `.repo/git`.

Use:

```bash
git --git-dir=.repo/git --work-tree=. status
git --git-dir=.repo/git --work-tree=. log --oneline
```

On a normal filesystem, this can be converted to a standard `.git` repository later.
