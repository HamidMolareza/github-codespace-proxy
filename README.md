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
- Run a Codespace-backed Xray proxy on one local mixed port, with HTTP and SOCKS5 both available on `127.0.0.1:8910`.
- Inspect operational activity, diagnostics, and correlation IDs.

The app reproduces the stable `sp-proxy` shape with native `gh` and OpenSSH: it starts/resumes the selected Codespace, verifies the remote proxy on `127.0.0.1:8899`, opens a hidden local SSH tunnel, and routes Xray through that tunnel. It does not rotate GitHub accounts to bypass quota.

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

Compose uses Linux host networking so `gh codespace ssh` follows the host VPN route. The backend image includes `gh`, `ssh`, and Xray, clears proxy variables for GitHub/Codespaces operations, sets `HOME=/app/data/home`, and stores generated Codespaces SSH config under `/app/data/codespaces-ssh`. The SSH config refresh is scoped to the selected Codespace and defaults to a 120 second timeout via `LocalProxy__CodespaceSshConfigTimeoutSeconds`.

Check the stack:

```bash
docker compose ps
curl http://127.0.0.1:5080/api/health
curl http://127.0.0.1:5080/api/diagnostics/runtime
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
