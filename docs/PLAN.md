# Codespace-Backed Proxy Plan

## Summary

The active workflow manages GitHub username/PAT records and runs a proxy through one automatically selected GitHub Codespace. The local gateway keeps one mixed HTTP/SOCKS port bound. The first proxy request selects the configured account with the lowest Codespaces usage, ensures the `wproxy97/proxy2` fork and Codespace exist, starts or resumes that Codespace, verifies the remote proxy on `127.0.0.1:8899`, opens an OpenSSH tunnel, starts Xray, and serves the request.

## Milestones

1. Keep GitHub account and Codespace snapshot persistence.
2. Use native `gh` and OpenSSH for Codespace tunnel orchestration.
3. Route Xray through the hidden Codespace tunnel instead of direct `freedom` outbound.
4. Expose one public port for both HTTP and SOCKS clients.
5. Show Codespace proxy progress and failures through Activity with redacted command output.
6. Keep only one local proxy settings profile and one active Codespace-backed runtime.
7. Stop extra running Codespaces during selection and stop the backing Codespace after proxy idle timeout.

## Acceptance Criteria

- A user can add, edit, view, and delete GitHub account records.
- A user can validate a PAT and sync Codespaces for that account.
- A user can send traffic to `http://127.0.0.1:8910` or `socks5h://127.0.0.1:8910` and have the app start the best available Codespace backend automatically.
- GitHub `409 Conflict` during start is treated as a refreshable lifecycle state instead of an unhandled exception.
- Limited accounts block create/start behavior through backend validation.
- Activity shows GitHub, tunnel, Xray, probe, and idle-stop events with redaction.
