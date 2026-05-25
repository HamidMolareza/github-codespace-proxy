# GitHub Codespaces Manager

Local admin panel for managing multiple GitHub accounts and running a GitHub Codespace-backed Xray proxy.

The application stores GitHub username/PAT records with ASP.NET Core Data Protection, validates tokens, syncs Codespaces, shows usage where GitHub exposes it, and provides create/start/stop/export/delete actions through official GitHub REST APIs.

## Scope

- Create, read, update, and delete GitHub account records.
- Store PATs encrypted at rest and never return PAT values from the API.
- Validate tokens with `GET /user`.
- Sync Codespaces with `GET /user/codespaces`.
- Create, start, stop, export, delete, and row-refresh Codespaces.
- Show usage from GitHub billing APIs when the token/account can access it.
- Block create/start when an account is marked `Limited`.
- Keep one local mixed proxy port bound, with HTTP and SOCKS5 both available on `127.0.0.1:8910`.
- On first proxy traffic, automatically select the configured account with the lowest Codespaces usage, ensure the `wproxy97/proxy2` fork and Codespace exist, stop extra running Codespaces, and start the tunnel-backed Xray proxy.
- Keep a lightweight wake gateway bound while the backend is idle, down, retrying, or starting. After idle auto-stop, the gateway waits for repeated wake traffic before spending Codespaces minutes again.
- Show clear proxy availability: `Up`, `Starting`, `Retrying`, `Idle`, or `Down`, with retry countdowns, latest request time, idle time, and idle shutdown time.
- Show Codespace proxy activity statistics for the last 24 hours, 7 days, or 30 days so idle-stop behavior can be audited.
- Show persisted latest user proxy requests in the Codespace Proxy tab with protocol, host, outcome, and Codespace context only.
- Retry Codespace proxy startup automatically after startup, tunnel, probe, or restart failures; allow manual retry from the Codespace Proxy tab.
- Remember the selected UI tab in the URL and allow `System`, `Light`, and `Dark` themes.
- Inspect operational activity, diagnostics, and correlation IDs.

The app reproduces the stable `sp-proxy` shape with native `gh`: it starts/resumes the selected Codespace, opens a tunnel from remote `127.0.0.1:8899` to a hidden local tunnel port, and routes Xray through that tunnel. The default tunnel mode is `gh codespace ports forward`; VPS deployments can switch to `LocalProxy__CodespaceTunnelMode=native-ssh` to generate an OpenSSH config with `gh codespace ssh --config` and run a plain `ssh -N -L` tunnel. Limited accounts are skipped, and idle shutdown stops the backing Codespace to reduce Codespaces usage. By default, a stopped idle proxy requires 5 proxy requests within 60 seconds before automatic wake; manual Retry starts immediately.

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

Host-network endpoints:

- Frontend: `127.0.0.1:5173`
- Backend API: `127.0.0.1:5080`
- Codespace proxy: `127.0.0.1:8910` for both HTTP and SOCKS5

Compose uses Linux host networking so `gh` follows the host VPN route. The backend image includes `gh`, `ssh`, and Xray, clears proxy variables for GitHub/Codespaces operations, sets `TZ=Asia/Tehran`, and sets `HOME=/app/data/home`. The gateway binds `127.0.0.1:8910` immediately; the first HTTP or SOCKS request starts the best available Codespace backend. Startup uses `gh codespace ports forward 8899:<hidden-port> -c <codespace>` by default, matching the working `sp-proxy` flow. Set `LocalProxy__CodespaceTunnelMode=native-ssh` to use `gh codespace ssh --config` plus native OpenSSH forwarding instead. Optional remote proxy verification/startup can be enabled with `LocalProxy__CodespaceEnsureRemoteProxy=true`.

Check the stack:

```bash
docker compose ps
curl http://127.0.0.1:5080/api/health
curl http://127.0.0.1:5080/api/diagnostics/runtime
curl http://127.0.0.1:5080/api/local-proxy/status
```

`/api/diagnostics/runtime` must show Xray, GitHub CLI, ssh, and GitHub direct networking as ready before a Codespace proxy can start.

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
- `DELETE /api/activity`
- `GET /api/diagnostics/runtime`

Every API response includes `X-Correlation-ID`. Incoming correlation IDs are preserved when the client sends that header. GitHub API paths/statuses, Codespace tunnel events, Xray proxy events, failures, and bounded snippets are recorded with secret redaction.

The Statistics tab reads `GET /api/local-proxy/statistics?period=24h`, `7d`, or `30d`. It uses app-managed local proxy sessions as the source of truth for active/off time, marks failed startup/runtime retry windows as red error time, and records GitHub Codespace state samples during existing sync and lifecycle operations. If GitHub reports a Codespace active while the app-managed proxy is not active, the tab highlights that mismatch.

## Codespace Proxy Status And Retry

The Codespace Proxy tab reads `GET /api/local-proxy/status`. The response includes the selected account/Codespace, status phase, availability, severity, retry countdown, latest request timestamp, public-port state, and latest session details.

Manual recovery is available with:

```bash
curl -X POST http://127.0.0.1:5080/api/local-proxy/retry
```

Use the Retry button when the panel shows `Down`, `Retrying`, or `Idle`. The automatic retry loop also recovers the latest failed Codespace-backed session after container restarts or transient internet/GitHub failures.

## UI Preferences

The frontend stores the selected tab in the URL query string. Refreshing `/?tab=local-proxy` or `/?tab=activity` returns to that tab.

Theme preference is stored in browser `localStorage` under `gh-proxy.theme`. Supported values are:

- `system`: follow the operating-system color scheme.
- `light`: force the light theme.
- `dark`: force the dark theme.
