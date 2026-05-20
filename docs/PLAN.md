# Local Proxy Manager Plan

## Summary

Pivot the active app workflow to a local-only proxy manager. The main UI creates local proxy profiles, starts an HTTP proxy listener on the backend host, supports HTTPS `CONNECT`, shows a ready endpoint, and records observable start/stop/probe/request events.

## Milestones

1. Add local proxy profile and session persistence.
2. Add local proxy API endpoints for CRUD, start, stop, active session, and probe.
3. Implement an in-process HTTP proxy with HTTPS `CONNECT`.
4. Add idle shutdown for inactive local proxy sessions.
5. Replace the active frontend with a local proxy dashboard.
6. Publish the proxy port through Docker Compose on host localhost.
7. Add backend tests and update run/operations docs.

## Acceptance Criteria

- A user can add, edit, view, and delete local proxy profiles.
- A user can click Start and receive a ready endpoint such as `http://127.0.0.1:8901`.
- Plain HTTP proxy requests are forwarded.
- HTTPS destinations work through `CONNECT`.
- Optional proxy authentication rejects unauthenticated requests.
- Stop closes the active listener.
- Idle shutdown stops inactive sessions.
- Activity shows start, stop, probe, auth, request, and idle events with redaction.
