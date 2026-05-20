# GH Proxy

Local admin panel for managing self-owned VPS proxy nodes.

The application runs on a trusted workstation and controls VPS nodes over SSH. It deploys an authenticated HTTP/SOCKS proxy with Docker Compose, starts a local tunnel on demand, and stops idle sessions automatically.

## Scope

- Manage VPS node records.
- Bootstrap proxy runtime over SSH.
- Start and stop a local proxy tunnel.
- Track activity and shut down idle sessions.

GitHub Codespaces account rotation is intentionally out of scope. Codespaces may be monitored or shut down safely, but this project does not automate quota bypass or multi-account rotation.

## Run Locally

Backend:

```bash
dotnet run --project src/GhProxy.Api/GhProxy.Api.csproj --urls http://127.0.0.1:5080
```

Frontend:

```bash
cd frontend
npm install
npm run dev
```

Open `http://127.0.0.1:5173`.

## Validate

```bash
dotnet build src/GhProxy.Api/GhProxy.Api.csproj --no-restore
dotnet test tests/GhProxy.Tests/GhProxy.Tests.csproj --no-build
cd frontend && npm run lint && npm run build
```

In this sandbox, the .NET test runner needs permission to open its local socket transport.
