# Codespace-Backed Proxy Plan

## Summary

The active workflow manages GitHub username/PAT records and runs a proxy through one automatically selected GitHub Codespace. The local gateway keeps one mixed HTTP/SOCKS port bound. The first proxy request reuses an existing account-owned `proxy*` Codespace when possible, preferring active Codespaces before stopped Codespaces, and creates a new Codespace only when no reusable proxy Codespace exists. It then starts or attaches to that Codespace, opens an OpenSSH dynamic SOCKS tunnel on a hidden local port, starts Xray through that tunnel, and serves the request.

## Milestones

1. Keep GitHub account and Codespace snapshot persistence.
2. Use `native-ssh` by default, with `ports-forward` and `ssh-direct` as configurable fallback/diagnostic tunnel modes.
3. Route Xray through the hidden Codespace tunnel instead of direct `freedom` outbound.
4. Expose one public port for both HTTP and SOCKS clients.
5. Show Codespace proxy progress and failures through Activity with redacted command output.
6. Keep only one local proxy settings profile and one active Codespace-backed runtime.
7. Stop extra running app-managed proxy Codespaces during selection, delete stopped app-managed proxy Codespaces when storage quota is limited, and stop the backing Codespace after proxy idle timeout.
8. Show proxy active/off statistics from app-managed sessions, with GitHub state samples only from existing sync and lifecycle calls.
9. Show aggregate compute quota runway from all added Free/Pro accounts, GitHub billing reset date, and recent app-managed proxy usage.

## Acceptance Criteria

- A user can add, edit, view, and delete GitHub account records.
- A user can validate a PAT and sync Codespaces for that account.
- A user can send traffic to `http://127.0.0.1:8910` or `socks5h://127.0.0.1:8910` and have the app start the best available Codespace backend automatically.
- Automatic startup reuses active or stopped account-owned `proxy*` Codespaces before creating a new Codespace.
- Stopped account-owned `proxy*` Codespaces are deleted automatically when storage quota is limited.
- GitHub `409 Conflict` during start is treated as a refreshable lifecycle state instead of an unhandled exception.
- Limited accounts block create/start behavior through backend validation.
- Activity shows GitHub, tunnel, Xray, probe, and idle-stop events with redaction.
- Statistics shows 24-hour hourly and 7/30-day daily proxy activity, plus warnings when GitHub active samples do not match app-managed proxy activity.
- Codespaces shows an aggregate quota forecast with remaining compute, reset date, estimated daily compute, and estimated usable days until reset.
- Large binary downloads are documented as a known limitation for Codespaces-backed proxy mode; domains that can use direct VPS egress should not be forced through the Codespaces tunnel for bulk transfer.
