# Codespace-Backed Proxy Plan

## Summary

The active workflow manages GitHub username/PAT records and runs a proxy through the selected GitHub Codespace. One Run action starts or resumes the Codespace, verifies the remote proxy on `127.0.0.1:8899`, opens an `autossh` tunnel, starts Xray, and exposes one local mixed HTTP/SOCKS port.

## Milestones

1. Keep GitHub account and Codespace snapshot persistence.
2. Use native `gh` and `autossh` for host-local Codespace tunnel orchestration.
3. Route Xray through the hidden Codespace tunnel instead of direct `freedom` outbound.
4. Expose one public port for both HTTP and SOCKS clients.
5. Show Codespace proxy progress and failures through Activity with redacted command output.

## Acceptance Criteria

- A user can add, edit, view, and delete GitHub account records.
- A user can validate a PAT and sync Codespaces for that account.
- A user can click Run Proxy on a Codespace and use both `http://127.0.0.1:8901` and `socks5h://127.0.0.1:8901`.
- GitHub `409 Conflict` during start is treated as a refreshable lifecycle state instead of an unhandled exception.
- Limited accounts block create/start behavior through backend validation.
- Activity shows GitHub, tunnel, Xray, probe, and idle-stop events with redaction.
