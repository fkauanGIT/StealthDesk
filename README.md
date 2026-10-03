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

**v0.1 - Agent connectivity** ([#23](https://github.com/fkauanGIT/StealthDesk/issues/23)) is complete:
a Windows machine running the agent shows up in the server's device list, online while connected.

- The agent collects device information (name, CPU, memory, drives, IP and MAC addresses, logged-in users)
- On first run it creates an Ed25519 key pair and stores it with its settings; every heartbeat is signed with it
- It connects to the SignalR hub at `/hubs/agent`, retries with backoff and jitter, and sends a heartbeat every
  5 minutes (10 seconds in debug)
- The server verifies each signature, rejects stale messages and key changes, and saves the device
- Devices are marked offline when their agent disconnects, and a default tenant is created on startup
- Devices can be listed through the REST API

**v0.2 - Live device dashboard** ([#24](https://github.com/fkauanGIT/StealthDesk/issues/24)) is complete: the
browser shows every device and keeps it current without refreshing.

- The device list shows name, OS, CPU, memory and storage use, logged-on users, status and last seen
- A device turns online or offline on the page within seconds, through a SignalR hub for browsers at `/hubs/dashboard`
- Clicking a device opens its details: disks, IP and MAC addresses, agent version and more, also updated live
- The page shows whether it is live or reconnecting, and reloads the list after reconnecting
- Devices are marked offline when the server starts, and agents the server forgot register again on their own

Next up: **v0.3**, user accounts.

### Roadmap

| Version | Goal | Epic |
|---|---|---|
| v0.1 | Agent connectivity | [#23](https://github.com/fkauanGIT/StealthDesk/issues/23) |
| v0.2 | Live device dashboard | [#24](https://github.com/fkauanGIT/StealthDesk/issues/24) |
| v0.3 | User accounts | [#25](https://github.com/fkauanGIT/StealthDesk/issues/25) |
| v0.4 | Permissions and access | [#100](https://github.com/fkauanGIT/StealthDesk/issues/100) |
| v0.5 | Remote terminal | [#26](https://github.com/fkauanGIT/StealthDesk/issues/26) |
| v0.6 | Remote desktop (MVP) | [#27](https://github.com/fkauanGIT/StealthDesk/issues/27) |

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

### Accounts

Account rules come from the `Accounts` section of `appsettings.json`:

| Key | Default | Meaning |
|---|---|---|
| `EnableBearerLogin` | `false` | Lets scripts sign in for bearer tokens. Browsers always use a cookie. |
| `BearerTokenLifetime` | `01:00:00` | How long a bearer token lasts. |
| `RefreshTokenLifetime` | `30.00:00:00` | How long a refresh token lasts. |
| `RequireUniqueEmail` | `true` | When false, several accounts may share an email. |

Passwords need at least 8 characters with an upper-case letter, a lower-case letter and a digit. Five wrong
passwords in a row lock the account for five minutes.

## Running the Agent

The agent runs on Windows. With the server running (see above), open a second terminal and start it:

```
dotnet run --project agent/StealthDesk.Agent -- run
```

In a few seconds the machine appears in the device list with `isOnline: true`:

```
curl http://localhost:5099/api/v1/devices
```

Stop the agent with `Ctrl+C` and the device is marked offline; start it again and the same device comes back
online.

On its first run the agent creates its key pair, and the server assigns the device an id with the first
accepted report. Both are saved and reused on every later run: that is the device's identity.

| What | Where |
|---|---|
| Server address | `Agent:ServerUrl` in [`agent/StealthDesk.Agent/appsettings.json`](./agent/StealthDesk.Agent/appsettings.json), or `--server <url>` |
| Identity (device id, tenant, private key) | `C:\ProgramData\StealthDesk\Debug\default\agent-settings.json` |
| Logs (one file per day, kept for 7 days) | `C:\ProgramData\StealthDesk\Debug\default\logs\` |

Debug builds use the `Debug` folder so development never touches an installed agent. Use `--instance <name>`
(`-i`) to run more than one agent on the same machine, each with its own folder instead of `default`.

## Endpoints

| Path | Transport | Purpose |
|------|-----------|---------|
| `/hubs/agent` | WebSockets | SignalR gateway agents stay connected to. Carries their signed device reports. |
| `/hubs/dashboard` | WebSockets | SignalR hub browsers connect to for live device updates. |
| `/api/v1/devices` | HTTP | Lists the devices known to the server. |
| `/api/v1/devices/{id}` | HTTP | Returns one device, or 404 if the server doesn't know it. |
| `/api/auth/login` | HTTP | Signs in. `?useCookies=true` sets the browser cookie; without it, returns bearer tokens when `Accounts:EnableBearerLogin` is on. |
| `/api/auth/me` | HTTP | The signed-in user and tenant, or 401. |
| `/api/auth/sign-out` | HTTP | Ends the cookie session. |
| `/api/auth/*` | HTTP | The other ASP.NET Core Identity endpoints: refresh, confirm email, forgot and reset password, manage info. |
| `/api/internal/version/server` | HTTP | Returns the server version. |
| `/health` | HTTP | Readiness check: every registered health check must pass. |
| `/alive` | HTTP | Liveness check: only checks tagged `liveness` must pass. |

## Running the Tests

The test projects use [xUnit v3](https://xunit.net/docs/getting-started/v3/whats-new), which builds each
project as an executable. Run them with `dotnet run`, not `dotnet test`:

```
dotnet run --project tests/StealthDesk.Shared.Tests
dotnet run --project tests/StealthDesk.Server.Tests
dotnet run --project tests/StealthDesk.Agent.Tests
dotnet run --project tests/StealthDesk.Web.Client.Tests
```

The gateway and device storage tests run against a real PostgreSQL started in a Docker container by
[Testcontainers](https://dotnet.testcontainers.org/), so Docker must be running; a local PostgreSQL
installation is not needed. Agent tests that call Windows APIs are skipped on other systems. Web client pages are
tested with [bUnit](https://bunit.dev/), which renders them without a browser. CI runs every
test project on each pull request and on every push to `main`, with a separate job for the Windows tests.

## Repository Layout

| Path | Contents |
|---|---|
| `frontend/` | `StealthDesk.Web.Client`: Blazor WebAssembly front end. `Ui/` holds the design system components and `wwwroot/css/tokens.css` its colors, type and spacing |
| `backend/` | `StealthDesk.Web.Server`: ASP.NET Core server with the agent gateway, REST API and EF Core database |
| `agent/` | `StealthDesk.Agent` (console executable), `StealthDesk.Agent.Core` (settings, identity, connection, heartbeat) and `StealthDesk.Agent.Windows` (device inventory through Windows APIs) |
| `shared/` | Used by both sides: `Contracts` (messages and the gateway interface), `Core` (message signing, retry backoff), `Realtime` (typed SignalR channel), `Hosting` (file logging), `Observability` (health checks, OpenTelemetry) and `Branding` |
| `tests/` | `StealthDesk.Shared.Tests`, `StealthDesk.Server.Tests`, `StealthDesk.Agent.Tests`, `StealthDesk.Web.Client.Tests` |

## Contributing

Every change starts from an issue and reaches `main` through a pull request. See
[CONTRIBUTING.md](./CONTRIBUTING.md) for the full workflow.
