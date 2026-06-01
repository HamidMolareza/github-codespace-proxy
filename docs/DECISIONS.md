# Decisions

## Architecture

- Backend: ASP.NET Core 10 Web API.
- Frontend: React, TypeScript, and Vite.
- Database: SQLite through EF Core.
- GitHub integration: direct `HttpClient` calls to official GitHub REST API endpoints.
- Active control plane location: trusted local workstation or Linux host-network Docker Compose stack.
- Active product mode: multi-account GitHub Codespaces manager with a Codespace-backed Xray proxy.
- Proxy mode: backend-managed Xray process routed through the selected Codespace. The default tunnel is a long-lived OpenSSH `ssh -N -L` forward; `ports-forward` and per-connection `ssh-direct` are explicit alternatives.

## Local Proxy Boundaries

- The default HTTP proxy endpoint is `http://127.0.0.1:8910`.
- The default SOCKS proxy endpoint is `socks5h://127.0.0.1:8910`.
- Xray exits through the selected Codespace remote proxy on `127.0.0.1:8899`.
- Docker Compose uses host networking so Codespace tunnel execution follows the host VPN route.
- Optional proxy authentication is passed to Xray HTTP and SOCKS inbounds from protected password storage.
- The app does not run a VPS tunnel.
- Codespaces-backed proxy mode is not treated as reliable bulk-transfer infrastructure. Large downloads should use direct egress when the surrounding gateway can route the destination directly.
- In the Arvan gateway deployment, GitHub/Codespaces control traffic must not
  use proxy-router. It follows host company-VPN routes directly and should fail
  closed if the required destinations are not routed through `ppp*`.

## GitHub API Boundaries

- Token validation uses `GET /user`.
- Codespace sync uses `GET /user/codespaces`.
- Codespace creation uses `POST /repos/{owner}/{repo}/codespaces`.
- Codespace start/stop/export/delete use authenticated-user Codespaces endpoints.
- Billing usage uses `GET /users/{username}/settings/billing/usage/summary?product=Codespaces` when available.

The app assumes the selected Codespace repository provides the proxy workload. It runs tunnel commands against user-authorized Codespaces and does not rotate accounts to bypass quota. Low or unavailable quota data is informational; accounts marked limited block create/start actions in this app.

## Security

- Store GitHub PATs with ASP.NET Core Data Protection.
- Never return PAT values from API responses.
- Redact PAT-like values, bearer/basic authorization headers, passwords, and URL credentials before logging or persisting operational events.
- Keep `.env`, tokens, database files, JSONL logs, and Data Protection key material out of git.

## Observability

- Use local-first observability: SQLite plus JSONL files and a React Activity tab.
- Preserve or generate `X-Correlation-ID` for every API request.
- Store GitHub API request paths, response status codes, and bounded redacted error snippets.
- Keep OpenTelemetry/Prometheus out of v1; add external exporters later only behind explicit configuration.

## Git Layout Note

This workspace uses a standard `.git/` directory. Use normal Git commands:

```bash
git <command>
```
