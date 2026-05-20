# VPS Proxy Panel Plan

## Summary

Build a local admin panel using ASP.NET Core/.NET 10 and React/Vite. The app manages self-owned VPS proxy nodes over SSH, deploys a Docker Compose based `3proxy` service, starts a local tunnel on `127.0.0.1:8901`, and stops idle sessions after 30 minutes of inactivity.

## Milestones

1. Initialize repository and documentation.
2. Scaffold backend, frontend, and tests.
3. Add persistence and VPS node CRUD.
4. Add SSH, Docker Compose, and tunnel control services.
5. Build the React admin panel.
6. Add structured local observability, runtime diagnostics, and Activity UI.
7. Validate and document operations.

## Acceptance Criteria

- A user can add, edit, view, and delete VPS nodes.
- A user can bootstrap a node using SSH and Docker Compose.
- A user can start and stop the local proxy tunnel from the panel.
- The app stops the active tunnel and remote proxy after the configured idle window.
- Secrets are redacted from logs and never committed.
- The Activity tab shows recent events, command failures, diagnostics, and correlation IDs.
- Operational events are persisted in SQLite and optional JSONL files with bounded redacted output.
