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

## GitHub Codespaces Workflow

1. Open `http://127.0.0.1:5173`.
2. Add a GitHub username and PAT in the Codespaces tab.
3. Click validate to confirm the PAT belongs to the expected GitHub account.
4. Click sync to load Codespaces for that account.
5. Use create/stop/export/delete/refresh from the Codespaces table.
6. Use `127.0.0.1:8910` as the local proxy. The gateway starts the best available Codespace automatically when the first HTTP or SOCKS request arrives.

PAT values are encrypted at rest and are not displayed after save.

The app uses Codespaces as the proxy backend only for accounts you add and authorize. It prefers the configured account with the lowest Codespaces usage, skips accounts marked limited, and stops extra running Codespaces so only one Codespace backend remains active.

## Codespace Proxy Workflow

1. Open `http://127.0.0.1:5173`.
2. Open the Codespace Proxy tab and review the single proxy settings profile.
3. Keep `Bind host` as `127.0.0.1` for direct local runs.
4. Keep `Proxy port` as `8910` unless that port is already in use.
5. Set username/password only if you want proxy authentication.
6. Send traffic to the proxy port. The backend selects the lowest-usage account, ensures the `wproxy97/proxy2` fork and Codespace exist, starts/resumes the Codespace, opens the configured Codespace tunnel to `8899:<hidden-port>`, starts Xray through that hidden tunnel, and serves the waiting request.
7. Watch the Codespace Proxy panel and Activity tab for selected account, selected Codespace, tunnel, Xray, probe, retry, reconnect, and idle-stop events.

The Automation Status card distinguishes `Up`, `Starting`, `Retrying`, `Idle`, and `Down`. It also shows the latest request time, idle duration, idle-stop countdown, retry countdown, selected account, and selected Codespace when available.

The Latest Requests card shows recent user traffic entering the Codespace proxy gateway. It is persisted in SQLite, records only protocol, host, port, outcome, duration, and session/Codespace context, and intentionally does not store URL paths, query strings, headers, bodies, credentials, or internal API/dashboard/probe requests.

Manual retry:

```bash
curl -X POST http://127.0.0.1:5080/api/local-proxy/retry
```

Use this when the panel is `Down`, `Retrying`, or `Idle` and you want to restart immediately instead of waiting for traffic or the retry timer.

Manual probe:

```bash
curl -x http://127.0.0.1:8910 http://example.com/
curl --socks5-hostname 127.0.0.1:8910 http://example.com/
```

Shell proxy exports:

```bash
export HTTP_PROXY=http://127.0.0.1:8910
export HTTPS_PROXY=http://127.0.0.1:8910
export http_proxy=http://127.0.0.1:8910
export https_proxy=http://127.0.0.1:8910
export ALL_PROXY=socks5h://127.0.0.1:8910
export all_proxy=socks5h://127.0.0.1:8910
export NO_PROXY=localhost,127.0.0.1
export no_proxy=localhost,127.0.0.1
```

## Docker Compose

The repository includes `compose.yml` for running both services with Linux host networking:

- `backend`: ASP.NET Core API bound to `127.0.0.1:5080` on the host network.
- `backend` gateway listener: bound to `127.0.0.1:8910` by default.
- `frontend`: Node serving the built React app on `127.0.0.1:5173` on the host network.
- `gh-proxy-data`: named volume for SQLite, JSONL logs, and Data Protection state.

Start:

```bash
docker compose up --build -d
```

Open:

```text
http://127.0.0.1:5173
```

In Docker mode, Compose uses `network_mode: host` so Codespaces traffic follows the connected host VPN. Compose sets `LocalProxy__BindHostOverride=127.0.0.1` so the gateway remains local-only. The backend image includes `gh`, `ssh`, and Xray, clears proxy variables for GitHub/Codespaces operations, and sets `HOME=/app/data/home`.

Smoke test:

```bash
docker compose ps
curl http://127.0.0.1:5080/api/health
curl http://127.0.0.1:5080/api/activity/summary
curl http://127.0.0.1:5080/api/diagnostics/runtime
```

The runtime diagnostics endpoint must report Xray, GitHub CLI, ssh, and GitHub direct networking as ready before the gateway can start a Codespace-backed local proxy.

Stop:

```bash
docker compose down
```

Remove persisted app data only when you intentionally want a clean database:

```bash
docker compose down -v
```

## Idle Auto-Stop And Recovery

`LocalProxyIdleShutdownService` stops the active local Xray proxy and the backing Codespace when there is no user traffic through the public proxy gateway for the profile idle window. Internal API calls, dashboard polling, readiness probes, runtime diagnostics, GitHub sync/billing calls, and Xray access-log writes do not extend the idle window. The gateway remains bound, but after an idle auto-stop it holds automatic wake until repeated proxy traffic proves real demand.

The default idle window is stored per profile and defaults to 30 minutes.

By default, automatic wake requires 5 user proxy requests within 60 seconds after idle stop. Tune this with `LocalProxy__IdleWakeRequestThreshold` and `LocalProxy__IdleWakeWindowSeconds`; set the threshold to `1` to restore single-request wake behavior. Manual Retry in the Codespace Proxy tab bypasses the threshold.

If the app restarts while an in-memory Codespace-backed proxy session is active, startup recovery attempts to restart the last saved Codespace session. If GitHub, internet, tunnel, or local probe failures occur, the backend schedules exponential retries and reports `Retrying` plus the countdown in `/api/local-proxy/status`.

Status check:

```bash
curl http://127.0.0.1:5080/api/local-proxy/status
```

## Codespace Proxy Statistics

Use the Statistics tab to confirm when the app-managed Codespace proxy was active, off, or failing to connect. The tab supports last 24 hours, 7 days, and 30 days. The 24-hour view is hourly; the longer views are daily.

The backend calculates active time from non-error `LocalProxySessions`, which are the app-managed Xray/tunnel sessions. Failed startup/runtime events are shown as error time from the first failure until the next successful `local_proxy.xray.started`, stop, or idle-stop event. The chart uses a gray default track, green for active time, red for error/retry downtime, and black for idle/off time. It also stores GitHub Codespace state samples during normal sync and lifecycle calls. The app does not add high-frequency GitHub polling for statistics. If GitHub reports a Codespace active while the app-managed proxy is inactive, the Statistics tab shows a warning so you can inspect that period in Activity or GitHub.

API check:

```bash
curl 'http://127.0.0.1:5080/api/local-proxy/statistics?period=24h'
```

## Observability

The Activity panel shows recent proxy events, runtime diagnostics, and redacted output snippets. Use it first when start, stop, probe, or proxy traffic does not behave as expected.

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

Secrets are redacted before command output, command display strings, details JSON, and error messages are persisted.

Use the Activity tab Clear button, or call `DELETE /api/activity`, to delete persisted Activity rows from SQLite and `operational-*.jsonl` files from the configured log directory.

## GitHub Codespaces

The Codespaces tab uses official GitHub REST APIs for normal lifecycle management. The automatic proxy startup then opens a Codespace tunnel from remote `127.0.0.1:8899` to a hidden local tunnel port, then runs Xray behind the public gateway port.

By default, startup verifies `gh codespace ssh -c <codespace> true`, then opens `gh codespace ports forward 8899:<hidden-port> -c <codespace>`. Set `LocalProxy__CodespaceTunnelMode=native-ssh` to generate an OpenSSH config with `gh codespace ssh --config` and run `ssh -N -L 127.0.0.1:<hidden-port>:127.0.0.1:8899` instead. Set `LocalProxy__CodespaceRequireSshReady=false` when the separate readiness command is unreliable but forwarding itself works. Remote proxy verification/startup is skipped by default because the working manual `sp-proxy` flow expects the Codespace proxy to already listen on `8899`; set `LocalProxy__CodespaceEnsureRemoteProxy=true` to ask the app to run the configured `proxy` command inside the Codespace first. If the tunnel exits unexpectedly, the panel reports `Retrying` or `Down`, and the next proxy request or manual Retry triggers startup again. If the idle window is reached, the panel reports `Idle`/`ZzzIdle`; automatic wake is thresholded and the backing Codespace is stopped to save usage.

## UI Preferences

The selected tab is stored in the URL. For example, refresh `http://127.0.0.1:5173/?tab=local-proxy` to return directly to the Codespace Proxy tab.

The theme selector in the top bar supports:

- `System`: follow `prefers-color-scheme`.
- `Light`: force the light theme.
- `Dark`: force the dark theme.

The selected theme is stored in browser `localStorage` as `gh-proxy.theme`.

## Git Commands In This Workspace

This workspace uses a standard `.git/` directory. Use normal Git commands:

```bash
git status
git log --oneline
```
