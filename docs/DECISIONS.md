# Decisions

## Architecture

- Backend: ASP.NET Core 10 Web API.
- Frontend: React, TypeScript, and Vite.
- Database: SQLite through EF Core.
- GitHub integration: direct `HttpClient` calls to official GitHub REST API endpoints.
- Control plane location: trusted local workstation or VPS.

## GitHub API Boundaries

- Token validation uses `GET /user`.
- Codespace sync uses `GET /user/codespaces`.
- Codespace creation uses `POST /repos/{owner}/{repo}/codespaces`.
- Codespace start/stop/export/delete use authenticated-user Codespaces endpoints.
- Billing usage uses `GET /users/{username}/settings/billing/usage/summary?product=Codespaces` when available.

The app does not create or manage proxy workloads, does not run tunnel commands against Codespaces, and does not rotate accounts to bypass quota. Low or unavailable quota data is informational; accounts marked limited block create/start actions in this app.

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

This workspace has a read-only `.git` mount. The repository metadata is stored in `.repo/git` and commands in this implementation use:

```bash
git --git-dir=.repo/git --work-tree=. <command>
```
