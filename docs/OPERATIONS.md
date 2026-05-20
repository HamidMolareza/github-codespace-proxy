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
- `gh-proxy-data`: named volume for SQLite, JSONL logs, and SSH known-hosts.

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

The backend container mounts `${HOME}/.ssh` as read-only at `/root/.ssh`. When adding a node from the Dockerized app, use an SSH key path that exists inside the container, for example `/root/.ssh/id_rsa`. The app writes SSH known hosts to `/app/data/known_hosts`, not into your mounted SSH directory.

The Docker stack sets `ProxyRuntime__TunnelCommandName=ssh` to avoid depending on `autossh` inside the image. Local non-Docker runs still default to `autossh`.

Run `curl http://127.0.0.1:5080/api/diagnostics/runtime` after startup. If `ssh`, `scp`, or `ss` is missing in your local .NET SDK base image, extend `src/GhProxy.Api/Dockerfile` with a reachable apt mirror and install `openssh-client` and `iproute2`, or run the API directly on the host.

## VPS Requirements

Each node should have:

- SSH access with a key file available on the workstation.
- Docker and Docker Compose V2.
- Outbound network access from the VPS.
- A normal Linux shell that can run commands through non-interactive SSH.

The app stores the SSH key path, not the key contents.

## Node Lifecycle

1. Add a VPS node in the panel.
2. Click bootstrap to upload `docker-compose.yml` and `3proxy.cfg` into `~/.gh-proxy` on the VPS.
3. Click start to run the remote proxy and create a local tunnel.
4. Configure clients to use `127.0.0.1:<local port>`.
5. Click stop to shut down the local tunnel and remote proxy.

The generated Docker Compose file binds the remote proxy ports to `127.0.0.1` on the VPS. Clients should connect through the local SSH tunnel, not directly to a public VPS proxy port.

## Idle Shutdown

The background worker checks active local TCP connections on the tunnel port with `ss`. If no activity is seen for the configured idle window, it kills the local tunnel process and runs `docker compose down` on the VPS.

Configuration lives under `ProxyRuntime` in `src/GhProxy.Api/appsettings.json`.

## Observability

The Activity tab shows recent operational events, command failures, runtime diagnostics, and redacted command output snippets. Use it first when a bootstrap, SSH/SCP copy, Docker Compose action, tunnel start, or idle shutdown does not behave as expected.

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

Runtime diagnostics check local tool availability for `ssh`, `scp`, `autossh`, and `ss`. A missing `autossh` means the app can still manage node records, but it cannot keep the local tunnel alive.

Secrets are redacted before command output, command display strings, details JSON, and error messages are persisted. Do not paste raw proxy passwords or PAT-like tokens into node notes or shell commands.

## Git Commands In This Workspace

The sandbox has a read-only `.git` mount, so this implementation stores repository metadata under `.repo/git`.

Use:

```bash
git --git-dir=.repo/git --work-tree=. status
git --git-dir=.repo/git --work-tree=. log --oneline
```

On a normal filesystem, this can be converted to a standard `.git` repository later.
