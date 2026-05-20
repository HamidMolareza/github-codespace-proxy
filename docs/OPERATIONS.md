# Operations Guide

## Local Control Plane

Run the API on the workstation:

```bash
dotnet run --project src/GhProxy.Api/GhProxy.Api.csproj --urls http://127.0.0.1:5080
```

Run the frontend:

```bash
cd frontend
npm install
npm run dev
```

The frontend proxies `/api/*` to `http://127.0.0.1:5080`.

## VPS Requirements

Each node should have:

- SSH access with a key file available on the workstation.
- Docker and Docker Compose V2.
- Outbound network access from the VPS.
- A normal Linux shell that can run commands through non-interactive SSH.

The app stores the SSH key path, not the key contents.

## Node Lifecycle

1. Add a VPS node in the panel.
2. Click bootstrap to upload `docker-compose.yml` and `3proxy.cfg` into `~/.gh-proxy` on the VPS.
3. Click start to run the remote proxy and create a local tunnel.
4. Configure clients to use `127.0.0.1:<local port>`.
5. Click stop to shut down the local tunnel and remote proxy.

The generated Docker Compose file binds the remote proxy ports to `127.0.0.1` on the VPS. Clients should connect through the local SSH tunnel, not directly to a public VPS proxy port.

## Idle Shutdown

The background worker checks active local TCP connections on the tunnel port with `ss`. If no activity is seen for the configured idle window, it kills the local tunnel process and runs `docker compose down` on the VPS.

Configuration lives under `ProxyRuntime` in `src/GhProxy.Api/appsettings.json`.

## Git Commands In This Workspace

The sandbox has a read-only `.git` mount, so this implementation stores repository metadata under `.repo/git`.

Use:

```bash
git --git-dir=.repo/git --work-tree=. status
git --git-dir=.repo/git --work-tree=. log --oneline
```

On a normal filesystem, this can be converted to a standard `.git` repository later.

