# Local Proxy Manager

Local admin panel for running a proxy listener on the same machine as the app.

The application stores local proxy profiles in SQLite, starts an HTTP proxy on a configurable localhost port, supports HTTPS through `CONNECT`, records operational activity, and stops idle sessions automatically.

## Scope

- Create, read, update, and delete local proxy profiles.
- Start, stop, restart by stopping and starting, and probe the active proxy.
- Expose a local HTTP proxy endpoint, default `http://127.0.0.1:8901`.
- Support optional proxy Basic authentication.
- Forward plain HTTP requests and HTTPS `CONNECT` tunnels.
- Track active connections, request counts, last activity, idle timeout, and errors.
- Inspect operational activity, diagnostics, and correlation IDs.

The proxy exits through the same machine/network where the backend runs. It does not provide a different external IP unless that machine already uses another network path such as a VPN.

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

Open `http://127.0.0.1:5173`, create a profile, and click Start.

Test the active proxy:

```bash
curl -x http://127.0.0.1:8901 http://example.com/
```

## Run With Docker Compose

Build and start both services:

```bash
docker compose up --build -d
```

Open `http://127.0.0.1:5173`.

Published ports:

- Frontend: `127.0.0.1:5173`
- Backend API: `127.0.0.1:5080`
- Local proxy: `127.0.0.1:8901`

In Docker mode, the backend binds the proxy listener to `0.0.0.0` inside the container, while Compose publishes it only to host localhost.

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
dotnet test tests/GhProxy.Tests/GhProxy.Tests.csproj --no-restore
cd frontend && npm run lint && npm run build
docker compose config
```

## Observability

The API writes structured operational events to SQLite and, by default, JSONL files under `data/logs/`. The frontend Activity panel reads:

- `GET /api/activity`
- `GET /api/activity/summary`
- `GET /api/diagnostics/runtime`

Every API response includes `X-Correlation-ID`. Incoming correlation IDs are preserved when the client sends that header. Proxy start/stop/probe results, request failures, auth failures, idle stops, and bounded command output are recorded with secret redaction.
