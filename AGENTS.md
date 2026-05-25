# AGENTS.md

## Project

`gh-proxy` is an ASP.NET Core 10 API plus React/Vite frontend for managing GitHub Codespaces and a local Codespace-backed Xray proxy.

## Repository Layout

- Backend: `src/GhProxy.Api`
- Frontend: `frontend`
- Tests: `tests/GhProxy.Tests`
- Operational docs: `docs`
- Docker stack: `compose.yml`

## Git

This workspace uses a standard `.git/` directory. Use normal Git commands:

```bash
git status
git diff
git add <paths>
git commit -m "type: message"
```

Commit messages should use Conventional Commits. Do not commit secrets, databases, JSONL logs, Docker volumes, `bin/`, `obj/`, `node_modules/`, or `.env` files.

## Validation

Use the existing target frameworks and package settings. Do not upgrade frameworks or replace the frontend stack unless explicitly requested.

Preferred checks before committing:

```bash
dotnet test GhProxy.sln -p:NuGetAudit=false
cd frontend && npm run lint && npm run build
docker compose config
```

For frontend-only changes, `npm run lint`, `npm run build`, and a browser smoke test are enough unless backend contracts changed.

## Runtime Notes

- Frontend: `http://127.0.0.1:5173`
- Backend API: `http://127.0.0.1:5080`
- Local proxy: `127.0.0.1:8910` for both HTTP and SOCKS5.
- Docker Compose uses host networking and sets `TZ=Asia/Tehran`.
- The wake gateway can be listening while the real Codespace/Xray backend is still starting or retrying; check `/api/local-proxy/status` for the authoritative state.

## Implementation Guidelines

- Keep code, comments, commit messages, and docs in English.
- Keep changes focused; avoid unrelated refactors.
- Prefer existing service, endpoint, DTO, and React component patterns.
- Keep the proxy status messages precise: distinguish `Up`, `Starting`, `Retrying`, `Idle`, and `Down`.
- When changing proxy lifecycle behavior, update README and `docs/OPERATIONS.md`.
- When adding API behavior, add or update focused tests under `tests/GhProxy.Tests`.
