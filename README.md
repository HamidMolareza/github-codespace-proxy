# GitHub Codespaces Manager

Local admin panel for managing multiple GitHub accounts and normal Codespaces lifecycle, with a separate local-only proxy tool.

The application stores GitHub username/PAT records with ASP.NET Core Data Protection, validates tokens, syncs Codespaces, shows usage where GitHub exposes it, and provides create/start/stop/export/delete actions through official GitHub REST APIs.

## Scope

- Create, read, update, and delete GitHub account records.
- Store PATs encrypted at rest and never return PAT values from the API.
- Validate tokens with `GET /user`.
- Sync Codespaces with `GET /user/codespaces`.
- Create, start, stop, export, delete, and row-refresh Codespaces.
- Show usage from GitHub billing APIs when the token/account can access it.
- Block create/start when an account is marked `Limited`.
- Provide a separate local proxy listener on `127.0.0.1:8901`.
- Inspect operational activity, diagnostics, and correlation IDs.

The app does not use GitHub Codespaces as a proxy backend, does not run `sp-proxy`, and does not rotate GitHub accounts to bypass quota.

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

Published ports:

- Frontend: `127.0.0.1:5173`
- Backend API: `127.0.0.1:5080`
- Local proxy: `127.0.0.1:8901`

In Docker mode, the backend binds the local proxy listener to `0.0.0.0` inside the container, while Compose publishes it only to host localhost.

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
dotnet build tests/GhProxy.Tests/GhProxy.Tests.csproj --no-restore
dotnet test tests/GhProxy.Tests/GhProxy.Tests.csproj --no-build
cd frontend && npm run lint && npm run build
docker compose config
```

## Observability

The API writes structured operational events to SQLite and, by default, JSONL files under `data/logs/`. The frontend Activity panel reads:

- `GET /api/activity`
- `GET /api/activity/summary`
- `GET /api/diagnostics/runtime`

Every API response includes `X-Correlation-ID`. Incoming correlation IDs are preserved when the client sends that header. GitHub API paths/statuses, local proxy events, failures, and bounded snippets are recorded with secret redaction.
