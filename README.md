# GH Proxy

Local admin panel for managing self-owned VPS proxy nodes.

The application runs on a trusted workstation and controls VPS nodes over SSH. It deploys an authenticated HTTP/SOCKS proxy with Docker Compose, starts a local tunnel on demand, and stops idle sessions automatically.

## Scope

- Manage VPS node records.
- Bootstrap proxy runtime over SSH.
- Start and stop a local proxy tunnel.
- Track activity and shut down idle sessions.

GitHub Codespaces account rotation is intentionally out of scope. Codespaces may be monitored or shut down safely, but this project does not automate quota bypass or multi-account rotation.

