# Decisions

## Architecture

- Backend: ASP.NET Core 10 Web API.
- Frontend: React, TypeScript, and Vite.
- Database: SQLite through EF Core.
- Runtime control: local backend starts `ssh`, `scp`, and `autossh` processes.
- Node proxy engine: `3proxy` in Docker Compose.
- Control plane location: local workstation.

## Security

- Do not store GitHub PATs in v1.
- Store SSH key paths, not private key contents.
- Protect generated proxy passwords with ASP.NET Core Data Protection.
- Redact secrets in API responses, logs, command output, and documentation.

## Observability

- Use local-first observability in v1: SQLite plus JSONL files and a React Activity tab.
- Preserve or generate `X-Correlation-ID` for every API request.
- Store command stdout/stderr as bounded redacted snippets, not raw unbounded output.
- Keep OpenTelemetry/Prometheus out of v1; add external exporters later only behind explicit configuration.

## Git Layout Note

This workspace has a read-only `.git` mount. The repository metadata is stored in `.repo/git` and commands in this implementation use:

```bash
git --git-dir=.repo/git --work-tree=. <command>
```
