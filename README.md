<p align="center">
  <img src=".github/assets/Banner-StealthDesk.png" alt="StealthDesk" width="800" />
</p>

[![CI](https://github.com/fkauanGIT/StealthDesk/actions/workflows/ci.yml/badge.svg)](https://github.com/fkauanGIT/StealthDesk/actions/workflows/ci.yml)

StealthDesk is a self-hosted remote device management platform built with .NET 10 and ASP.NET Core.
A lightweight agent runs on each managed machine and keeps a signed, real-time SignalR connection to
the server, which tracks every device and, in later versions, lets technicians reach them from the browser.

> **Early development:** until v1.0 there may be breaking changes between versions. The API has no
> authentication until v0.3, so don't expose the server to the internet.

## Project Status

**v0.1 - Agent connectivity** ([#23](https://github.com/fkauanGIT/StealthDesk/issues/23)) is in progress.
The server side is done:

- Agents connect to the SignalR hub at `/hubs/agent` and send heartbeats signed with Ed25519
- The server verifies each signature, rejects stale messages and key changes, and saves the device
- Devices are marked offline when their agent disconnects
- A default tenant is created on startup
- Devices can be listed through the REST API

Next up: the agent itself (a console app that connects, reports device info, and sends periodic heartbeats).

### Roadmap

| Version | Goal | Epic |
|---|---|---|
| v0.1 | Agent connectivity | [#23](https://github.com/fkauanGIT/StealthDesk/issues/23) |
| v0.2 | Live device dashboard | [#24](https://github.com/fkauanGIT/StealthDesk/issues/24) |
| v0.3 | User authentication | [#25](https://github.com/fkauanGIT/StealthDesk/issues/25) |
| v0.4 | Remote terminal | [#26](https://github.com/fkauanGIT/StealthDesk/issues/26) |
| v0.5 | Remote desktop (MVP) | [#27](https://github.com/fkauanGIT/StealthDesk/issues/27) |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [PostgreSQL](https://www.postgresql.org/download/) (optional: the server can run with an in-memory database)
- [Docker](https://www.docker.com/products/docker-desktop/) to run the server tests that use PostgreSQL
- [dotnet-ef](https://learn.microsoft.com/en-us/ef/core/cli/dotnet), only to create new migrations:
  `dotnet tool install --global dotnet-ef`

## Quick Start

### In-memory database

The fastest way to start the server. Data is lost when it stops.

```
git clone https://github.com/fkauanGIT/StealthDesk.git
cd StealthDesk
dotnet run --project backend/StealthDesk.Web.Server --launch-profile http -- --UseInMemoryDatabase=true
```

The server listens on `http://localhost:5099`. Running `curl http://localhost:5099/health` should
return `Healthy`.

### PostgreSQL

The connection is built from these settings, found in
[`appsettings.Development.json`](./backend/StealthDesk.Web.Server/appsettings.Development.json):

| Key | Default |
|---|---|
| `POSTGRES_HOST` | `localhost` |
| `POSTGRES_PORT` | `5432` |
| `POSTGRES_DB` | `stealthdesk` |
| `POSTGRES_USER` | `postgres` |
| `POSTGRES_PASSWORD` | `password` |

Change them to match your PostgreSQL instance, or override them without editing the file through
environment variables (e.g. `POSTGRES_PASSWORD`) or command-line arguments (`--POSTGRES_PASSWORD=...`).
Then start the server. It applies the pending migrations on startup, creating the database if needed:

```
dotnet run --project backend/StealthDesk.Web.Server --launch-profile http
```

## Endpoints

| Path | Transport | Purpose |
|------|-----------|---------|
| `/hubs/agent` | HTTP and WebSockets | SignalR connection used by agents. Carries device registration and heartbeats (MessagePack). |
| `/api/v1/devices` | HTTP | Lists the devices known to the server. |
| `/api/internal/version/server` | HTTP | Returns the server version. |
| `/health` | HTTP | Readiness check: every registered health check must pass. |
| `/alive` | HTTP | Liveness check: only checks tagged `live` must pass. |

## Running the Tests

The test projects use [xUnit v3](https://xunit.net/docs/getting-started/v3/whats-new), which builds each
project as an executable. Run them with `dotnet run`, not `dotnet test`:

```
dotnet run --project tests/StealthDesk.Libraries.Shared.Tests
dotnet run --project tests/StealthDesk.Web.Server.Tests
```

Most tests use an in-memory database. The device manager and agent heartbeat tests run against a real
PostgreSQL started in a Docker container by [Testcontainers](https://dotnet.testcontainers.org/), so
Docker must be running; a local PostgreSQL installation is not needed. CI runs every test project on each
pull request and on every push to `main`.

## Repository Layout

| Path | Contents |
|---|---|
| `frontend/` | `StealthDesk.Web.Client`: Blazor WebAssembly front end |
| `backend/` | `StealthDesk.Web.Server`: ASP.NET Core server with the agent hub, REST API and EF Core database |
| `agent/` | `StealthDesk.Agent.Common` (hub connection and heartbeat), `StealthDesk.Agent.Shared` (agent configuration and device information) and the Windows native interop library |
| `shared/` | Code used by both the server and the agent: API contracts, signing, branding, the typed SignalR client, hosting, logging and `StealthDesk.Web.ServiceDefaults` (health checks, OpenTelemetry, resilience defaults) |
| `tests/` | One test project per project under test |

## Contributing

Every change starts from an issue and reaches `main` through a pull request. See
[CONTRIBUTING.md](./CONTRIBUTING.md) for the full workflow.
