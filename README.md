# GitHub Codespaces Manager

Local admin panel for managing multiple GitHub accounts and their Codespaces.

The application runs on a trusted workstation or VPS, stores GitHub username/PAT pairs with ASP.NET Core Data Protection, validates tokens, lists Codespaces, shows best-effort usage data, and controls normal Codespaces lifecycle actions through the official GitHub REST API.

## Scope

- Create, read, update, and delete GitHub account records.
- Store PATs encrypted at rest and never return PAT values from the API.
- Validate tokens with `GET /user`.
- Sync Codespaces with `GET /user/codespaces`.
- Create, start, stop, export, and delete Codespaces with official GitHub API endpoints.
- Show usage from GitHub billing usage APIs when the token/account can access them.
- Auto-stop idle running Codespaces after the configured idle window.
- Inspect operational activity, GitHub API failures, diagnostics, and correlation IDs.

This project does not implement Codespaces-as-proxy, port forwarding, proxy process management, quota-bypass rotation, or automatic switch-to-next-account behavior.

## Run Locally

Backend:

```bash
dotnet run --project src/GhProxy.Api/GhProxy.Api.csproj --urls http://127.0.0.1:5080
```

Frontend:

```bash
cd frontend
npm install
npm run dev
```

Open `http://127.0.0.1:5173`.

## Run With Docker Compose

Build and start both services:

```bash
docker compose up --build -d
```

Open `http://127.0.0.1:5173`. The frontend container serves the built React app and proxies `/api/*` to the backend container.

The backend API is also published directly at `http://127.0.0.1:5080`.

Check the stack:

```bash
docker compose ps
curl http://127.0.0.1:5080/api/health
curl http://127.0.0.1:5080/api/diagnostics/runtime
```

Stop it:

```bash
docker compose down
```

Data is stored in the named Docker volume `gh-proxy_gh-proxy-data`.

## Validate

```bash
dotnet build GhProxy.sln --no-restore
dotnet test tests/GhProxy.Tests/GhProxy.Tests.csproj --no-restore
cd frontend && npm run lint && npm run build
docker compose config
```

## Observability

The API writes structured operational events to SQLite and, by default, JSONL files under `data/logs/`. The frontend Activity tab reads:

- `GET /api/activity`
- `GET /api/activity/summary`
- `GET /api/diagnostics/runtime`

Every API response includes `X-Correlation-ID`. Incoming correlation IDs are preserved when the client sends that header. GitHub API paths, status codes, failures, and bounded response snippets are recorded with secret redaction.
