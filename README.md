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
- Devices are marked offline when their agent disconnects
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
| `EnablePublicRegistration` | `false` | Lets anyone register at any time; each registration gets its own tenant. |
| `DisableFirstUserSelfRegistration` | `false` | Closes the registration a new server allows for its first user. |
| `RequireConfirmedEmail` | `false` | Users must confirm their email before signing in. Needs email sending. |
| `PersistPasskeySignIn` | `false` | A passkey sign-in keeps the session after the browser closes, like "Remember me". |
| `MicrosoftClientId`, `MicrosoftClientSecret` | empty | Sign in with a Microsoft account. On only when both are set. |
| `GitHubClientId`, `GitHubClientSecret` | empty | Sign in with GitHub. On only when both are set. |

A new server has no users and no tenants. The first person to register (`POST /api/auth/register`) gets a new
tenant and becomes the server administrator; after that, registration is closed unless public registration is on.
Agents can only join once that first tenant exists.

Passwords need at least 8 characters with an upper-case letter, a lower-case letter and a digit. Five wrong
passwords in a row lock the account for five minutes.

#### Signing in with Microsoft or GitHub

Each provider needs an app registered with it, whose callback address points back to this server:

- **GitHub**: Settings → Developer settings → OAuth Apps → New OAuth App. Homepage `http://localhost:5099`,
  callback `http://localhost:5099/signin-github`. Copy the client ID and generate a client secret.
- **Microsoft**: Azure portal → App registrations → New registration, for personal and work accounts. Add a Web
  redirect URI `http://localhost:5099/signin-microsoft`, then create a client secret under Certificates & secrets.

Keep the secrets out of `appsettings.json`, e.g. with user secrets:

```powershell
dotnet user-secrets set "Accounts:GitHubClientId" "<client id>" --project backend/StealthDesk.Web.Server
dotnet user-secrets set "Accounts:GitHubClientSecret" "<client secret>" --project backend/StealthDesk.Web.Server
```

A first sign-in with a provider follows the registration rules above, creating an account without a password.
Providers can be linked and unlinked in the account settings, as long as the account keeps a way to sign in.

### Email

Account emails (confirm the address, reset the password, confirm a new email) go out by SMTP, configured in the
`Email` section:

| Key | Default | Meaning |
|---|---|---|
| `DisableSending` | `false` (`true` in development) | Sends nothing. In development the messages, links included, are written to the server log. |
| `SmtpHost`, `SmtpPort` | empty, `587` | The SMTP server. Port 465 uses TLS from the start; others use STARTTLS when offered. |
| `SmtpUserName`, `SmtpPassword` | empty | Credentials, when the server needs them. |
| `SenderName`, `SenderAddress` | `StealthDesk`, empty | Who the messages come from. |

The first user's email is confirmed right away, and so is everyone's while sending is disabled. A message that can't
be delivered is logged and can be asked for again; it never fails the registration or the reset. The server refuses
to start with `Accounts:RequireConfirmedEmail` on and sending disabled.

## Running the Agent

The agent runs on Windows. Agents join a tenant, so on a new server create the first account before starting one:
open `http://localhost:5099`, choose **Create an account** on the sign-in page and register. You become the server
administrator and land on the device list.

With the server running (see above), open a second terminal and start the agent:

```
dotnet run --project agent/StealthDesk.Agent -- run
```

In a few seconds the machine appears in the list as online, without refreshing the page. Stop the agent with
`Ctrl+C` and it turns offline; start it again and the same device comes back online.

Scripts can do the same through the API (in PowerShell):

```powershell
$account = @{ email = "admin@example.com"; password = "Choose-a-Passw0rd" } | ConvertTo-Json
Invoke-RestMethod http://localhost:5099/api/auth/register -Method Post -Body $account -ContentType "application/json"
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Invoke-WebRequest "http://localhost:5099/api/auth/login?useCookies=true" -Method Post -Body $account -ContentType "application/json" -WebSession $session | Out-Null
Invoke-RestMethod http://localhost:5099/api/v1/devices -WebSession $session
```

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
| `/hubs/dashboard` | WebSockets | SignalR hub signed-in browsers connect to for live updates of their tenant's devices. |
| `/api/v1/devices` | HTTP | Lists the devices of the signed-in user's tenant; 401 when signed out. |
| `/api/v1/devices/{id}` | HTTP | Returns one of the tenant's devices, or 404 for an unknown device or one of another tenant. |
| `/api/auth/login` | HTTP | Signs in. `?useCookies=true` sets the browser cookie; without it, returns bearer tokens when `Accounts:EnableBearerLogin` is on. |
| `/api/auth/register` | HTTP | Registers following the account rules above; 404 while registration is closed. |
| `/api/auth/settings` | HTTP | Whether registration is open, for the sign-up page. |
| `/api/auth/me` | HTTP | The signed-in user and tenant, or 401. |
| `/api/auth/sign-out` | HTTP | Ends the cookie session. |
| `/api/auth/*` | HTTP | The other ASP.NET Core Identity endpoints: refresh, confirm email, forgot and reset password, manage info. |
| `/api/account/profile` | HTTP | The signed-in user's own account (`GET`), and saving the phone number (`PUT`). |
| `/api/account/password` | HTTP | Changes the password; other sessions are signed out. `/password/set` adds one to an account without it. |
| `/api/account/personal-data` | HTTP | Downloads the user's personal data as JSON. |
| `/api/account/delete` | HTTP | Deletes the signed-in account; needs the password when the account has one. |
| `/api/account/two-factor/*` | HTTP | Two-factor with an authenticator app: status, the key and QR code, turning it on and off, resetting the key, new recovery codes, forgetting this browser. |
| `/api/auth/two-factor` | HTTP | The second sign-in step with the app's code, after `/login` answered `RequiresTwoFactor`. `/api/auth/recovery-code` does the same with a recovery code. |
| `/api/account/passkeys` | HTTP | The user's passkeys: list (`GET`), add (`POST`, after `POST /creation-options`), rename (`PUT /{id}`) and remove (`DELETE /{id}`). |
| `/api/auth/passkey` | HTTP | Signs in with a passkey, after `POST /api/auth/passkey/request-options`. Needs no second factor. |
| `/api/auth/external/{provider}` | HTTP | Leaves for Microsoft or GitHub to sign in; the provider comes back to `/signin-microsoft` or `/signin-github`, then to `/api/auth/external/callback`. `/pending` and `/register` create the account on a first visit. |
| `/api/account/logins` | HTTP | The providers linked to the account: list (`GET`), link (`GET /link/{provider}`), unlink (`DELETE /{provider}/{key}`). |

A user marked to change their password gets `403` from the rest of the API and the hubs until they change it.
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
