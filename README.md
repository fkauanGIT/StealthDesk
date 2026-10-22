<p align="center">
  <img src=".github/assets/Banner-StealthDesk.png" alt="StealthDesk" width="800" />
</p>

[![CI](https://github.com/fkauanGIT/StealthDesk/actions/workflows/ci.yml/badge.svg)](https://github.com/fkauanGIT/StealthDesk/actions/workflows/ci.yml)
[![Project board](https://img.shields.io/badge/project-board-blue?logo=github)](https://github.com/users/fkauanGIT/projects/3)

StealthDesk is a self-hosted remote device management platform built with .NET 10 and ASP.NET Core.
A lightweight agent runs on each managed machine and keeps a signed, real-time SignalR connection to
the server, which tracks every device and, in later versions, lets technicians reach them from the browser.

> **Early development:** until v1.0 there may be breaking changes between versions. Permissions decide what each
> user can see and do since v0.4, but the server has not been hardened for the internet yet.

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

**v0.3 - User accounts** ([#25](https://github.com/fkauanGIT/StealthDesk/issues/25)) is complete: people sign in
from the browser, and each tenant sees only its own devices.

- The first person to register becomes the server administrator; public registration and email confirmation are
  optional, and account emails go out by SMTP
- Sign-in with a password, a passkey (Windows Hello, a phone or a security key) or a Microsoft or GitHub account
- Two-factor authentication with an authenticator app, recovery codes and remembered browsers
- Account settings: profile, email change, password, passkeys, linked accounts, two-factor, personal data download
  and account deletion
- The device API and the dashboard hub require sign-in, and devices and users are kept per tenant
- Account journeys are tested end to end on PostgreSQL, and browser tests with Playwright run in their own workflow

**v0.4 - Permissions and access** ([#100](https://github.com/fkauanGIT/StealthDesk/issues/100)) is complete:
permissions decide what each person, script and agent install can do, and tenants run themselves.

- Permissions are allowed or denied to a user, a user group, a personal access token or a service account, at
  server, tenant, device group or device scope; a deny always wins, and presets such as Tenant Administrator group
  the usual ones
- Every API route and dashboard hub method checks its permission, and the device list, each device and live
  updates agree on what a user may read
- Every permission change is kept in an authorization log, per tenant and for the server
- Tenants manage their users, invites, user groups, device groups and settings; server administrators manage
  tenants and a server-wide alert, and send a test email
- Personal access tokens and service accounts let scripts use the API with their own, limited permissions
- Devices join with installer keys (limited uses, time-limited or persistent), optionally into a device group, from
  an "Add device" page that waits for the device to appear
- The web app is organized by task (Devices, People & access, Integrations, Activity, Server), with a light theme
- Every route is tested with and without its permission and from another tenant, on PostgreSQL

Next up: **v0.5**, deployment.

### Roadmap

| Version | Goal | Epic |
|---|---|---|
| v0.1 | Agent connectivity | [#23](https://github.com/fkauanGIT/StealthDesk/issues/23) |
| v0.2 | Live device dashboard | [#24](https://github.com/fkauanGIT/StealthDesk/issues/24) |
| v0.3 | User accounts | [#25](https://github.com/fkauanGIT/StealthDesk/issues/25) |
| v0.4 | Permissions and access | [#100](https://github.com/fkauanGIT/StealthDesk/issues/100) |
| v0.5 | Deployment | [#127](https://github.com/fkauanGIT/StealthDesk/issues/127) |
| v0.6 | Remote terminal | [#26](https://github.com/fkauanGIT/StealthDesk/issues/26) |
| v0.7 | Remote desktop (MVP) | [#27](https://github.com/fkauanGIT/StealthDesk/issues/27) |

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
dotnet run --project StealthDesk.Web.Server --launch-profile http -- --UseInMemoryDatabase=true
```

The server listens on `http://localhost:5099`. Running `curl http://localhost:5099/health` should
return `Healthy`.

### PostgreSQL

The connection is built from the `POSTGRES_*` [settings](#server-settings).
[`appsettings.Development.json`](./StealthDesk.Web.Server/appsettings.Development.json) points them at
`localhost:5432`, database `stealthdesk`, user `postgres` and password `password`. Change them to match your
PostgreSQL instance, or override them without editing the file through environment variables
(`StealthDesk_POSTGRES_PASSWORD`) or command-line arguments (`--POSTGRES_PASSWORD=...`). Then start the server. It applies the pending migrations on startup, creating the database if needed:

```
dotnet run --project StealthDesk.Web.Server --launch-profile http
```

### Accounts

Account rules come from the `Accounts` [settings](#server-settings).

A new server has no users and no tenants. The first person to register (`POST /api/auth/register`) gets a new
tenant and becomes the server administrator; after that, registration is closed unless public registration is on.
A server set up by automation can instead get its first administrator from the `Bootstrap` settings, with a
personal access token and a server service account whose keys the automation chooses in advance; they are created
on the first start only, while the server has no users.
Agents join a tenant with one of its installer keys (see [Running the Agent](#running-the-agent)).

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
dotnet user-secrets set "Accounts:GitHubClientId" "<client id>" --project StealthDesk.Web.Server
dotnet user-secrets set "Accounts:GitHubClientSecret" "<client secret>" --project StealthDesk.Web.Server
```

A first sign-in with a provider follows the registration rules above, creating an account without a password.
Providers can be linked and unlinked in the account settings, as long as the account keeps a way to sign in.

### Tenant settings

Each tenant can change a few settings on the **Settings** page (or `/api/v1/tenant-settings`); one it hasn't set
follows the server's default, from the `TenantDefaults` [settings](#server-settings).

### Email

Account emails (confirm the address, reset the password, confirm a new email) go out by SMTP, configured in the
`Email` [settings](#server-settings).

The first user's email is confirmed right away, and so is everyone's while sending is disabled. A message that can't
be delivered is logged and can be asked for again; it never fails the registration or the reset. The server refuses
to start with `Accounts:RequireConfirmedEmail` on and sending disabled.

### Behind a reverse proxy

Behind a reverse proxy (Nginx, Caddy, Cloudflare) the server sees the proxy's address and plain HTTP unless it trusts
the proxy. From a trusted proxy it takes the client's address, scheme and host from `X-Forwarded-For`,
`X-Forwarded-Proto` and `X-Forwarded-Host` before anything else runs, so cookies are `Secure`, sign-in redirects go
back to `https://` and logs record the client. A chain of proxies (Cloudflare in front of Nginx) is followed back as
long as each one is trusted. Only a proxy on the same machine is trusted until the `ReverseProxy`
[settings](#server-settings) name others.

The proxy must pass WebSocket upgrades (the `Upgrade` and `Connection` headers) to `/hubs/agent` and
`/hubs/dashboard`. Agents use nothing but `/hubs/agent`, and prove who they are with signed reports and installer
keys. When the proxy puts its own sign-in in front of the server (Cloudflare Access, for instance), leave that path
open; browsers need the rest.

### Encrypting the data protection keys

Sign-in cookies, bearer tokens, two-factor steps and emailed links are protected with data protection keys kept in
the database. Stored as plain XML, anyone who reads the database or a backup of it can forge a sign-in cookie, so in
Production the server warns at startup until they are encrypted with a certificate: a `.pfx` file with an RSA
private key. Create one with PowerShell:

```powershell
$certificate = New-SelfSignedCertificate -Subject "CN=StealthDesk keys" -KeyAlgorithm RSA -KeyLength 3072 `
  -KeyExportPolicy Exportable -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(10)
Export-PfxCertificate -Cert $certificate -FilePath stealthdesk-keys.pfx -Password (Read-Host -AsSecureString "Password")
Remove-Item "Cert:\CurrentUser\My\$($certificate.Thumbprint)"
```

or with OpenSSL:

```bash
openssl req -x509 -newkey rsa:3072 -nodes -keyout keys.pem -out keys.crt -days 3650 -subj "/CN=StealthDesk keys"
openssl pkcs12 -export -inkey keys.pem -in keys.crt -out stealthdesk-keys.pfx
rm keys.pem keys.crt
```

Then turn on `KeyProtection:EncryptKeys` and point `KeyProtection:Certificate:Path` at the file (or put its base64
contents in `KeyProtection:Certificate:Base64`), with its password in `KeyProtection:Certificate:Password`. Keep the
file and its password out of the database backups, or encrypting gains nothing. The server refuses to start when the
certificate is missing, can't be read or has no private key. Keys written before stay readable; new ones are
encrypted. The certificate's expiry date doesn't matter.

To rotate the certificate, create a new one, make it the `Certificate`, and move the old one to
`KeyProtection:PreviousCertificates:0` (`:Path`, `:Base64`, `:Password`, like the current one). Previous certificates
only read the keys they encrypted, so nobody is signed out. Keys last 90 days, and sessions made with them up to
30 more, so keep the old certificate for at least four months; dropped sooner, those sessions and links stop
working and their users sign in again. Turning encryption off works the same way: keep the certificate as a previous
one until its keys are gone.

### Server settings

Every setting the server reads, by key. A key with a section is written `Section:Key` on the command line
(`--Accounts:EnablePublicRegistration=true`) and nested in `appsettings.json`. In a container, set it as an
environment variable with the `StealthDesk_` prefix and `__` between section and key
(`StealthDesk_Accounts__EnablePublicRegistration=true`), or as a Docker secret: with `EnableDockerSecrets` on, each
file in `/run/secrets` is a setting, named the same way without the prefix (`Accounts__GitHubClientSecret`). A secret
wins over a variable, which wins over the files.

The server checks every setting when it starts and stops with a message naming the key of any invalid one. Outside
the Development environment it also refuses the in-memory database and the development PostgreSQL password.

| Key | Default | Meaning |
|---|---|---|
| `UseInMemoryDatabase` | `false` | An in-memory database instead of PostgreSQL, lost when the server stops. Development and tests only. |
| `InMemoryDatabaseName` | random | Servers in one process with the same name share the in-memory data. |
| `POSTGRES_HOST` | `localhost` in development | PostgreSQL's address. Required. |
| `POSTGRES_PORT` | `5432` | |
| `POSTGRES_DB` | `stealthdesk` | Created if missing; migrations run on every start. |
| `POSTGRES_USER`, `POSTGRES_PASSWORD` | `postgres`, `password` in development | Required. |
| `ApplyMigrationsOnStart` | `true` | Brings the database schema up to date on every start. Turned off, the server only checks it is up to date and refuses to start otherwise. |
| `EnableDockerSecrets` | `false` | Also reads settings from files in `DockerSecretsDirectory`, which must exist. |
| `DockerSecretsDirectory` | `/run/secrets` | Where Docker mounts the secrets. |
| `Accounts:EnableBearerLogin` | `false` | Lets scripts sign in for bearer tokens. Browsers always use a cookie. |
| `Accounts:BearerTokenLifetime` | `01:00:00` | How long a bearer token lasts. |
| `Accounts:RefreshTokenLifetime` | `30.00:00:00` | How long a refresh token lasts. |
| `Accounts:RequireUniqueEmail` | `true` | When false, several accounts may share an email. |
| `Accounts:EnablePublicRegistration` | `false` | Lets anyone register at any time; each registration gets its own tenant. |
| `Accounts:DisableFirstUserSelfRegistration` | `false` | Closes the registration a new server allows for its first user. |
| `Accounts:RequireConfirmedEmail` | `false` | Users must confirm their email before signing in. Needs email sending. |
| `Accounts:PersistPasskeySignIn` | `false` | A passkey sign-in keeps the session after the browser closes, like "Remember me". |
| `Accounts:DefaultTheme` | `Auto` | How the sign-in pages look: `Auto`, `Light` or `Dark`. Each user then picks their own. |
| `Accounts:MicrosoftClientId`, `Accounts:MicrosoftClientSecret` | empty | Sign in with a Microsoft account. On only when both are set. |
| `Accounts:GitHubClientId`, `Accounts:GitHubClientSecret` | empty | Sign in with GitHub. On only when both are set. |
| `Email:DisableSending` | `false` (`true` in development) | Sends nothing. In development the messages, links included, are written to the server log. |
| `Email:SmtpHost`, `Email:SmtpPort` | empty, `587` | The SMTP server. Port 465 uses TLS from the start; others use STARTTLS when offered. |
| `Email:SmtpUserName`, `Email:SmtpPassword` | empty | Credentials, when the server needs them. |
| `Email:SmtpLocalDomain` | empty | The name the server introduces itself with; some relays require a real domain. |
| `Email:SmtpCheckCertificateRevocation` | `true` | Checks whether the SMTP server's certificate was revoked. |
| `Email:SenderName`, `Email:SenderAddress` | `StealthDesk`, empty | Who the messages come from. |
| `TenantDefaults:AppendInstanceName` | `false` | Install commands name an agent instance (`--instance`), so agents of several servers can share a machine. |
| `TenantDefaults:InstanceName` | empty | The instance they name; empty makes one from the server's address (`desk.example.com` becomes `desk-example-com`). |
| `Gateway:ClockTolerance` | `00:01:00` | How far an agent's clock may be from the server's before its reports are refused as replays. Empty turns the check off. |
| `InstallerKeys:UsageHistoryDays` | `90` | How long each key's installs are kept. Zero keeps them all. |
| `AuthorizationLogs:RetentionDays` | `365` | How long authorization log entries are kept. Zero keeps them all. |
| `ServiceAccounts:AuthFailureLimit`, `ServiceAccounts:AuthFailureWindowMinutes` | `5`, `5` | Failed service account sign-ins allowed per address and per credential before more are refused for that many minutes. |
| `ServiceAccounts:CredentialCleanupAfterDays` | `30` | Revoked and expired credentials are deleted this long after they stopped working. Zero keeps them. |
| `Bootstrap:AdminEmail`, `Bootstrap:AdminPassword` | empty | The first administrator, created on a start while the server has no users, like the first registration. Both or neither. |
| `Bootstrap:AdminTokenId`, `Bootstrap:AdminTokenSecret` | empty | A personal access token for that administrator, `{id}:{secret}`, acting as them and never expiring. The secret needs 32 characters or more. |
| `Bootstrap:ServerServiceAccountName`, `Bootstrap:ServerServiceAccountCredentialId`, `Bootstrap:ServerServiceAccountCredentialSecret` | empty | An unrestricted server service account and its key, `{credential id}:{secret}`, created while none has that name. All three or none; the secret needs 32 characters or more. |
| `Bootstrap:ServerServiceAccountId`, `Bootstrap:ServerServiceAccountDescription` | new id, empty | Its fixed id and description. |
| `AgentDownloads:Folder` | `downloads` | Where the agent builds the server hands out are, relative to the server's folder unless absolute; the Windows agent is `windows/StealthDesk.Agent.exe` in it. The container image ships one. |
| `ReverseProxy:TrustedProxies` | empty | Addresses of proxies whose forwarded headers are trusted, e.g. `10.0.0.2`. A list: `--ReverseProxy:TrustedProxies:0=10.0.0.2`, or `StealthDesk_ReverseProxy__TrustedProxies__0`. |
| `ReverseProxy:TrustedNetworks` | empty | Networks of trusted proxies, e.g. `10.0.0.0/8`. A list, like the one above. |
| `ReverseProxy:DockerGatewayAddress` | empty | The gateway of the Docker network the server runs in, which a proxy in front of Docker comes through. |
| `ReverseProxy:TrustCloudflare` | `false` | Trusts Cloudflare's address ranges, downloaded from Cloudflare when the server starts; it doesn't start if they can't be. |
| `ReverseProxy:TrustAllProxies` | `false` | Trusts forwarded headers from any address. Only for a server nothing but the proxy can reach: otherwise any client can claim any address. |
| `KeyProtection:EncryptKeys` | `false` | Encrypts the data protection keys with `KeyProtection:Certificate`. See [encrypting the keys](#encrypting-the-data-protection-keys). |
| `KeyProtection:Certificate:Path`, `KeyProtection:Certificate:Base64`, `KeyProtection:Certificate:Password` | empty | The `.pfx` certificate, with an RSA private key, from a file or its base64 contents (which wins), and its password. |
| `KeyProtection:PreviousCertificates` | empty | Certificates that only read keys encrypted before a rotation. A list, each with `Path` or `Base64` and `Password`: `KeyProtection:PreviousCertificates:0:Path`. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | empty | Sends logs, traces and metrics to this OpenTelemetry collector. |

## Running in a container

The server and its web client build into one image, from the repository root:

```bash
docker build -f StealthDesk.Web.Server/Dockerfile -t stealthdesk-server .
```

It runs on the ASP.NET runtime as the image's unprivileged `app` user, listens on port 8080 and takes every
[setting](#server-settings) as a `StealthDesk_` variable. With a PostgreSQL container next to it:

```bash
docker network create stealthdesk
docker run -d --name stealthdesk-db --network stealthdesk -e POSTGRES_PASSWORD=change-me postgres:17
docker run -d --name stealthdesk --network stealthdesk -p 8080:8080 \
  -e StealthDesk_POSTGRES_HOST=stealthdesk-db \
  -e StealthDesk_POSTGRES_USER=postgres \
  -e StealthDesk_POSTGRES_PASSWORD=change-me \
  stealthdesk-server
```

The container reports itself healthy once `/health/ready` answers, which needs the database. The server applies
database migrations as it starts; to apply them another way (for instance from a SQL script made with
`dotnet ef migrations script --idempotent`), turn off `ApplyMigrationsOnStart`. The image speaks plain HTTP: put a
[reverse proxy](#behind-a-reverse-proxy) in front of it for HTTPS.

### With Docker Compose

[`docker-compose/`](./docker-compose) runs the server and PostgreSQL together. The server waits for the database to
be healthy, both restart unless stopped, and the database lives in the `stealthdesk-database` volume. Users,
devices and the data protection keys are kept there, so removing and recreating the containers loses nothing.

```bash
cd docker-compose
cp .env.example .env    # set StealthDesk_POSTGRES_PASSWORD, and any other setting
docker compose up -d
```

`.env.example` lists every setting, commented out at its default. The server image is
`franciscokauan/stealthdesk`, pinned with `SERVER_IMAGE_TAG`; until it can be pulled, Compose builds it from this
repository. The server listens on `SERVER_PORT` (8080) and trusts forwarded headers from the network's gateway
(`DOCKER_GATEWAY`, 172.30.0.1), which a reverse proxy on the same host comes through. To encrypt the
[data protection keys](#encrypting-the-data-protection-keys), put the `.pfx` in `docker-compose/certificates`: the
server sees that folder, read-only, as `/certificates`.

To keep passwords out of variables, `docker-compose.secrets.yml` reads them from Docker secrets instead: one file per
setting in `docker-compose/secrets`, named after it (`Bootstrap__AdminPassword` holds `Bootstrap:AdminPassword`).
The key certificate goes in `KeyProtection__Certificate__Base64` as base64. An empty file leaves its setting off.
After a [rotation](#encrypting-the-data-protection-keys), add the previous certificate the same way, as two more
secrets of the server: `KeyProtection__PreviousCertificates__0__Base64` and `KeyProtection__PreviousCertificates__0__Password`.

```bash
cp -r secrets.example secrets    # write the database password in secrets/POSTGRES_PASSWORD
chmod 700 secrets                # the files stay readable inside the containers, which run as other users
docker compose -f docker-compose.secrets.yml up -d
```

A single line break at the end of a file is dropped, but a Windows one (CRLF) leaves a carriage return in the value:
save the files with Unix line endings, or write them with `printf '%s' 'the-password' > secrets/POSTGRES_PASSWORD`.

Both files can also start a dashboard for the server's logs, traces and metrics at `TELEMETRY_DASHBOARD_PORT`
(18888). Set `StealthDesk_OTEL_EXPORTER_OTLP_ENDPOINT=http://telemetry:18889`, then add `--profile telemetry`
to the `up` command. The dashboard asks for `TELEMETRY_DASHBOARD_TOKEN` (or the `Dashboard__Frontend__BrowserToken`
secret); left empty, it makes a new one on each start and logs a sign-in link (`docker compose logs telemetry`).

## Running the Agent

The agent runs on Windows. A new device joins a tenant with an installer key, so on a new server create the first
account before starting one: open `http://localhost:5099`, choose **Create an account** on the sign-in page and
register. You become the server administrator and land on the device list.

Choose **Add device** on the device list, create a key and copy the command it shows. From a second terminal, run
the agent with that key:

```
dotnet run --project StealthDesk.Agent -- run --installer-key <key>
```

In a few seconds the machine appears in the list as online, without refreshing the page. Stop the agent with
`Ctrl+C` and it turns offline; start it again, with or without the key, and the same device comes back online.

The key is only needed until the device joins: from then on the device signs its reports with its own key, and
revoking the installer key doesn't affect it. Keys are usage-based (a number of installs within 24 hours),
time-based (until they expire) or persistent (until revoked), and **Installer keys** lists each key's installs.
Installs are kept for `InstallerKeys:UsageHistoryDays` (90 by default; zero keeps them all). A reinstalled agent
brings a new signing key: the server accepts it for a known device only with a key whose creator has `agent.install`
there, and `--device-id <id>` names the device to take back.

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
| Server address | `Agent:ServerUrl` in [`StealthDesk.Agent/appsettings.json`](./StealthDesk.Agent/appsettings.json), or `--server <url>` |
| Installer key (never saved by the agent) | `Agent:InstallerKey`, or `--installer-key <key>` (`-k`) |
| Identity (device id, tenant, private key) | `C:\ProgramData\StealthDesk\Debug\default\agent-settings.json` |
| Logs (one file per day, kept for 7 days) | `C:\ProgramData\StealthDesk\Debug\default\logs\` |

Debug builds use the `Debug` folder so development never touches an installed agent. Use `--instance <name>`
(`-i`) to run more than one agent on the same machine, each with its own folder instead of `default`.

### As a Windows service

On a managed machine the agent runs as a service: it starts with Windows, before anyone signs in, and Windows
restarts it 5 seconds after it fails.

The **Add device** page gives the command that installs it, for PowerShell as an administrator on the machine. It
downloads the agent from the server (`/downloads/agent/windows`, open to anyone, since the machine has no account),
runs it only if its SHA-256 is the one the page named, deletes the download either way, and carries the installer key
but no password. The agent is a single executable with the .NET runtime inside, so the machine needs nothing else.
Running the command again updates an installed agent in place; it keeps the device's identity, so a key that was used
up still works.

The hash guards the download, not the server: it comes from the same server as the file, so it catches a file changed
on its way to the machine, but a server whose own build was replaced names the replacement's hash. Serve the page over
HTTPS. Telling a genuine build from any other takes code signing, which the agent doesn't have yet.

The container image ships that build. A server run from source hands it out once it is published into its downloads
folder:

```powershell
dotnet publish StealthDesk.Agent -p:PublishProfile=Windows -o StealthDesk.Web.Server/downloads/windows
```

Without it, the page says the server has no build and gives the command for an agent copied to the machine by hand,
to run from its folder:

```powershell
.\StealthDesk.Agent.exe install --server https://desk.example.com --installer-key <key>
```

Install copies the agent to `C:\Program Files\StealthDesk\default`, joins the server with the key and stops there if
the server refuses it, then creates the `StealthDesk.Agent` service (LocalSystem, automatic start), lists it in
Settings > Apps and starts it. The service itself never gets the key: the device joined already. Its settings,
private key and logs are in `C:\ProgramData\StealthDesk\default`, which only administrators and SYSTEM can open.
Stopping the service disconnects it, so the device shows offline at once.

```powershell
.\StealthDesk.Agent.exe uninstall
```

Uninstall, also run by **Uninstall** in Settings > Apps, removes the service, the installed files, the settings and
the device's key. `--instance` works with both, giving each instance its own service (`StealthDesk.Agent.<name>`)
and folders. Debug builds install as `StealthDesk.Agent.Debug` in `Debug` folders, apart from a real installation.

The agent isn't signed yet, so antivirus software that watches for remote access tools may quarantine it when it
installs as a service. `.github/scripts/agent-download-test.ps1` and `agent-service-test.ps1` check an installation end
to end on a machine where it can run.

## Endpoints

| Path | Transport | Purpose |
|------|-----------|---------|
| `/hubs/agent` | WebSockets | SignalR gateway agents stay connected to. Carries their signed device reports. |
| `/hubs/dashboard` | WebSockets | SignalR hub signed-in browsers connect to for live updates of their tenant's devices. |
| `/api/v1/devices` | HTTP | Lists the devices of the signed-in user's tenant; 401 when signed out. |
| `/api/v1/devices/{id}` | HTTP | Returns one of the tenant's devices, or 404 for an unknown device or one of another tenant. |
| `/api/v1/device-groups` | HTTP | The tenant's device groups: list, one with its devices, create, change, delete, and add, remove or move devices (`POST`/`DELETE /{id}/members`, `fromGroupId` to move). Device permissions can be granted on a group, reaching every device in it. |
| `/api/v1/tenants` | HTTP | The server's tenants, for its administrators: list (`GET`), one (`GET /{id}`), create (`POST`), rename (`PUT /{id}`) and delete with everything in them (`DELETE /{id}`, never the caller's own). |
| `/api/v1/server/alert` | HTTP | The message every signed-in user sees at the top of the app (`GET`), set by the server's administrators (`PUT`). |
| `/api/v1/server/test-email` | HTTP | Sends a test message to the administrator's own address and says whether it went out. |
| `/api/v1/tenant-settings` | HTTP | The tenant's settings: list (`GET`), one (`GET /{name}`), change one (`PUT /{name}`) or several (`PUT`), back to the default (`DELETE /{name}`). |
| `/api/v1/deployment-options` | HTTP | What install commands use, for those who install agents: the tenant's agent instance, and the Windows agent the server hands out with its size and SHA-256. |
| `/downloads/agent/windows` | HTTP | The Windows agent build, for anyone; 404 when the server has none. |
| `/api/v1/installer-keys` | HTTP | The tenant's installer keys: list (`GET`), create (`POST`), rename (`PUT /{id}`), revoke (`DELETE /{id}`) and installs (`GET /{id}/usages`). Without `installer-key.manage-all`, only your own. |
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
| `/api/internal/version/server` | HTTP | Returns the server version. |
| `/health/ready` | HTTP | Readiness: every check must pass, including that the database answers. `/health` does the same. |
| `/health/live` | HTTP | Liveness: only that the process responds, so a server waiting for its database isn't restarted. `/alive` does the same. |

A user marked to change their password gets `403` from the rest of the API and the hubs until they change it.

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

### UI tests

`tests/StealthDesk.Web.UITests` drives a real browser with [Playwright](https://playwright.dev/dotnet/) through
registration, sign-in, account settings, two-factor, passkeys and the external providers. Each test starts the built
server as its own process on a fresh PostgreSQL database, so build first and keep Docker running:

```
dotnet build StealthDesk.slnx
dotnet run --project tests/StealthDesk.Web.UITests
```

On Windows they use the installed Edge; elsewhere they install Playwright's Chromium on the first run.
`STEALTHDESK_UI_BROWSER` picks another browser channel (e.g. `chrome`) and `STEALTHDESK_UI_HEADED=1` shows the
window. Each test's last screen and the server's log land in `TestResults/StealthDesk.Web.UITests/`.

They take a few minutes, so CI leaves them out; the **UI tests** workflow runs them when started from the Actions
tab, and on pull requests that change them.

## Repository Layout

| Path | Contents |
|---|---|
| `StealthDesk.Web.Client/` | Blazor WebAssembly front end. `Ui/` holds the design system components and `wwwroot/css/tokens.css` its colors, type and spacing |
| `StealthDesk.Web.Server/` | ASP.NET Core server with the agent gateway, REST API and EF Core database |
| `StealthDesk.Agent/` | The agent's console executable, with `StealthDesk.Agent.Core/` (settings, identity, connection, heartbeat) and `StealthDesk.Agent.Windows/` (device inventory through Windows APIs) next to it |
| `shared/` | Used by both sides: `Contracts` (messages and the gateway interface), `Core` (message signing, retry backoff), `Realtime` (typed SignalR channel), `Hosting` (file logging), `Observability` (health checks, OpenTelemetry) and `Branding` |
| `tests/` | `StealthDesk.Shared.Tests`, `StealthDesk.Server.Tests`, `StealthDesk.Agent.Tests`, `StealthDesk.Web.Client.Tests`, and the browser tests in `StealthDesk.Web.UITests` |
| `docker-compose/` | Compose files that run the server with PostgreSQL, from variables or from Docker secrets |

## Contributing

The [project board](https://github.com/users/fkauanGIT/projects/3) shows the backlog and what's in progress, by
milestone. Every change starts from an issue and reaches `main` through a pull request. See
[CONTRIBUTING.md](./CONTRIBUTING.md) for the full workflow.

## License

StealthDesk is source-available under the [Apache License 2.0 with the Commons Clause](./LICENSE). Anyone, people
and companies alike, may use it, change it and share it for free, under the Apache License's terms. What the Commons
Clause takes away is the right to **sell** it: to charge others for StealthDesk itself, or for hosting or supporting
it when its value comes from StealthDesk. Selling it takes a separate commercial license from its author, Francisco
Kauan; ask through this repository.

Because of that condition, StealthDesk isn't open source in the Open Source Initiative's sense, although its code is
open to read.
