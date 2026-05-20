# GitHub Codespaces Manager Plan

## Summary

Pivot the app from a VPS proxy panel into a GitHub account and Codespaces admin panel. The active UI stores GitHub username/PAT records, validates tokens, syncs Codespaces, shows usage where GitHub exposes it, and provides normal Codespaces lifecycle actions.

This plan intentionally excludes proxy/tunnel automation, Codespaces-as-proxy behavior, quota-bypass rotation, and automatic switching between accounts.

## Milestones

1. Add GitHub account and Codespace snapshot persistence.
2. Add encrypted PAT storage and redacted GitHub API observability.
3. Add official GitHub REST API client for user validation, Codespaces sync, lifecycle actions, export, and billing usage.
4. Add safe maintenance automation: scheduled sync and idle auto-stop only.
5. Replace the active frontend with a Codespaces dashboard.
6. Update Docker/local docs and validation commands.
7. Add tests for schema creation, sync behavior, limited-account blocking, and secret redaction.

## Acceptance Criteria

- A user can add, edit, view, and delete GitHub account records.
- PAT values are encrypted at rest, redacted from logs, and never returned by API responses.
- A user can validate an account token.
- A user can sync and inspect Codespaces grouped by selected account.
- A user can create, start, stop, export, and delete a Codespace through the UI.
- Usage is displayed when GitHub billing APIs allow it; otherwise the UI clearly shows that usage is unavailable.
- The app can auto-stop idle Codespaces after the configured idle window.
- Operational events are persisted in SQLite and optional JSONL files with bounded redacted output.
