# Safe Multi-Account Codespaces Manager Plan

## Summary

The active app workflow manages multiple GitHub username/PAT records and normal Codespaces lifecycle. The UI has separate tabs for Codespaces, Local Proxy, and Activity. Codespaces are not used as proxy infrastructure.

## Milestones

1. Keep GitHub account and Codespace snapshot persistence.
2. Restore the Codespaces UI for account CRUD, validation, usage, sync, and lifecycle actions.
3. Add row refresh and lifecycle polling so start/stop/create progress is visible.
4. Keep local proxy as a separate Xray-backed direct local tool with HTTP and SOCKS endpoints.
5. Keep shared Activity and diagnostics for GitHub and local proxy events.
6. Update Docker/local docs and validation commands.
7. Add tests for Codespace row refresh and preserve local proxy tests.

## Acceptance Criteria

- A user can add, edit, view, and delete GitHub account records.
- A user can validate a PAT and sync Codespaces for that account.
- A user can create, start, stop, export, delete, and refresh a Codespace.
- Start/stop/create actions show progress and update final state.
- Limited accounts disable create/start behavior through backend validation.
- The Local Proxy tab continues to expose only a local proxy endpoint.
- Activity shows GitHub and local proxy operational events with redaction.
