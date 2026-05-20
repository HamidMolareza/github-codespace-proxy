# GH Proxy

Local admin panel for managing self-owned VPS proxy nodes.

The application runs on a trusted workstation and controls VPS nodes over SSH. It deploys an authenticated HTTP/SOCKS proxy with Docker Compose, starts a local tunnel on demand, and stops idle sessions automatically.

## Scope

- Manage VPS node records.
- Bootstrap proxy runtime over SSH.
- Start and stop a local proxy tunnel.
- Track activity and shut down idle sessions.
- Inspect operational activity, command failures, diagnostics, and correlation IDs.

GitHub Codespaces account rotation is intentionally out of scope. Codespaces may be monitored or shut down safely, but this project does not automate quota bypass or multi-account rotation.

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

Data is stored in the named Docker volume `gh-proxy_gh-proxy-data`. The Compose file mounts `${HOME}/.ssh` read-only at `/root/.ssh` so node SSH key paths can use `/root/.ssh/<key-name>` inside the container. The Docker profile uses `ssh` for tunnels; local workstation runs still default to `autossh`.

## Validate

```bash
dotnet build src/GhProxy.Api/GhProxy.Api.csproj --no-restore
dotnet test tests/GhProxy.Tests/GhProxy.Tests.csproj --no-build
cd frontend && npm run lint && npm run build
docker compose config
```

In this sandbox, the .NET test runner needs permission to open its local socket transport.

## Observability

The API writes structured operational events to SQLite and, by default, JSONL files under `data/logs/`. The frontend Activity tab reads:

- `GET /api/activity`
- `GET /api/activity/summary`
- `GET /api/diagnostics/runtime`

Every API response includes `X-Correlation-ID`. Incoming correlation IDs are preserved when the client sends that header. Command output is bounded and redacted before it is stored or returned.
