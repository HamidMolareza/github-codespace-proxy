# github-codespace-proxy

`github-codespace-proxy` is a local admin panel for running a GitHub Codespace-backed Xray proxy with automatic startup, idle shutdown, retry recovery, and usage visibility.

It is built as an ASP.NET Core API with a React/Vite frontend. The backend manages GitHub accounts and Codespaces through GitHub APIs and `gh`, then exposes one local mixed HTTP/SOCKS5 proxy port for client traffic.

## Features

- Manage multiple GitHub accounts and encrypted personal access tokens.
- Validate accounts, sync Codespaces, and show quota/usage information when GitHub exposes it.
- Create, start, stop, export, delete, and refresh Codespaces from the panel.
- Keep one local proxy endpoint available on `127.0.0.1:8910` for HTTP and SOCKS5.
- Start the best available Codespace automatically when real proxy traffic arrives.
- Stop the backing Codespace after idle time to reduce Codespaces cost.
- Prevent unwanted restart after idle stop by requiring repeated wake traffic before automatic wake.
- Retry startup/tunnel/probe failures and expose clear `Up`, `Starting`, `Retrying`, `Idle`, and `Down` states.
- Show latest user proxy requests with host-only details.
- Show activity statistics for active, idle/off, and error/retry downtime.
- Record operational events, diagnostics, and correlation IDs for troubleshooting.

## Requirements

- .NET SDK matching the solution target framework.
- Node.js and npm for the frontend.
- Docker with Compose v2 for the containerized stack.
- GitHub CLI (`gh`) authenticated with an account that can manage Codespaces.
- Xray available locally, or use the provided Docker image.

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

Default endpoints:

- Frontend: `http://127.0.0.1:5173`
- Backend API: `http://127.0.0.1:5080`
- Proxy: `127.0.0.1:8910`

## Run With Docker Compose

```bash
docker compose up --build -d
```

Compose uses host networking so `gh`, SSH, and tunnel traffic follow the host network route. The backend image includes `gh`, `ssh`, and Xray, sets `TZ=Asia/Tehran`, and stores app data under the `gh-proxy_gh-proxy-data` Docker volume.

In the Arvan gateway deployment, GitHub API, GitHub SSH, and Codespaces tunnel
control traffic must follow the host company-VPN route directly. The container
clears proxy environment variables for those operations; it must not bootstrap
itself through proxy-router. If the route guard cannot prove those destinations
use a `ppp*` interface, startup should fail or retry instead of using public VPS
egress.

If Docker cannot reach upstream registries directly, build with proxy variables from the shell:

```bash
HTTP_PROXY=http://127.0.0.1:8910 \
HTTPS_PROXY=http://127.0.0.1:8910 \
NO_PROXY=localhost,127.0.0.1 \
docker compose build
```

The backend and frontend runtime images both default to `node:22-bookworm-slim` in this repo to avoid the larger MCR devcontainer image. Override them independently with `GH_PROXY_BACKEND_RUNTIME_BASE` and `GH_PROXY_FRONTEND_BASE` when a deployment needs different runtime bases. Docker restore uses the Liara NuGet mirror by default; set `GH_PROXY_NUGET_RESTORE_SOURCE=https://api.nuget.org/v3/index.json` when you want to restore from official NuGet. The .NET SDK build stage still uses `mcr.microsoft.com/dotnet/sdk:10.0`; if Docker times out while loading metadata for that base image, configure the Docker daemon proxy or pre-pull the image through a working network route.

Check the stack:

```bash
docker compose ps
curl http://127.0.0.1:5080/api/health
curl http://127.0.0.1:5080/api/diagnostics/runtime
curl http://127.0.0.1:5080/api/local-proxy/status
```

Stop it:

```bash
docker compose down
```

## Codespace Proxy Flow

The gateway binds the public proxy port immediately. When user proxy traffic arrives, the backend selects the lowest-usage eligible account, ensures the configured proxy Codespace exists, starts or resumes it, opens a tunnel from remote `127.0.0.1:8899` to a hidden local port, and routes Xray through that tunnel.

The default tunnel mode uses an OpenSSH config from `gh codespace ssh --config`, then runs one long-lived OpenSSH local forward:

```bash
ssh -F <generated-config> -N -L 127.0.0.1:<hidden-port>:127.0.0.1:8899 <codespace-host>
```

Set `LocalProxy__CodespaceTunnelMode=ports-forward` to use `gh codespace ports forward`, or `LocalProxy__CodespaceTunnelMode=ssh-direct` to open one `ssh -W` bridge per local proxy connection.

The Codespaces tunnel is a control-plane convenience, not a guaranteed bulk-transfer transport. Large downloads are safest when the calling gateway can route the domain through direct VPS egress. Domains that must stay in `proxy` mode through GitHub Codespaces can still stall or truncate large binary responses even when the local relay code is healthy.

When gh-proxy is used by the Arvan proxy-router stack, it is only the upstream
for explicit `proxy` rules. Direct traffic and company CIDRs should remain on
the VPS company/direct route. If the Codespaces-backed proxy is down, matching
proxy-rule requests should fail rather than silently use another egress path.

After idle auto-stop, automatic wake is thresholded. By default, the app requires 5 proxy requests within 60 seconds before spending Codespaces minutes again. Manual Retry starts immediately.

## Observability

The Activity tab reads structured operational events from SQLite and JSONL logs. The Statistics tab reads:

```text
GET /api/local-proxy/statistics?period=24h|7d|30d
```

Statistics use app-managed local proxy sessions as the source of truth:

- Green: active time.
- Black: idle/off time.
- Red: error or retry downtime.
- Gray: chart background/default track.

The Codespace Proxy tab also shows latest user proxy requests. Request history stores only protocol, host, port, outcome, duration, and session/Codespace context. It does not store URL paths, query strings, headers, bodies, credentials, or internal dashboard/API/probe requests.

## Validate

```bash
dotnet test GhProxy.sln -p:NuGetAudit=false
cd frontend && npm run lint && npm run build
docker compose config
```

## Security Notes

- GitHub PATs are protected with ASP.NET Core Data Protection.
- PAT values are never returned by API responses.
- Command output and persisted operational events are redacted before storage.
- Keep `.env`, data volumes, SQLite databases, logs, and generated runtime files out of Git.

## License

This project is licensed under the GNU General Public License v3.0 or later. See [LICENSE.md](LICENSE.md).
