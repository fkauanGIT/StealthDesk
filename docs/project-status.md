# Project status and roadmap

> **Early development:** until v1.0 there may be breaking changes between versions.

## Roadmap

| Version | Goal | Epic |
|---|---|---|
| v0.1 | Agent connectivity | [#23](https://github.com/fkauanGIT/StealthDesk/issues/23) |
| v0.2 | Live device dashboard | [#24](https://github.com/fkauanGIT/StealthDesk/issues/24) |
| v0.3 | User accounts | [#25](https://github.com/fkauanGIT/StealthDesk/issues/25) |
| v0.4 | Permissions and access | [#100](https://github.com/fkauanGIT/StealthDesk/issues/100) |
| v0.5 | Deployment | [#127](https://github.com/fkauanGIT/StealthDesk/issues/127) |
| v0.6 | Remote terminal | [#26](https://github.com/fkauanGIT/StealthDesk/issues/26) |
| v0.7 | Remote desktop (MVP) | [#27](https://github.com/fkauanGIT/StealthDesk/issues/27) |

The [project board](https://github.com/users/fkauanGIT/projects/3) shows the backlog and what's in progress, and each
[release](https://github.com/fkauanGIT/StealthDesk/releases) has its notes.

## What each version brought

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

**v0.5 - Deployment** ([#127](https://github.com/fkauanGIT/StealthDesk/issues/127)) is complete: the server runs
on a company server, in a container behind HTTPS, and agents install as a Windows service from one command.

- A container image on Docker Hub (`franciscokauan/stealthdesk`), with Docker Compose files for the server and
  PostgreSQL, health checks and an optional telemetry dashboard
- Every setting can come from a `StealthDesk_` variable or a Docker secret, a wrong one stops the server at start
  with the setting named, and a first administrator and access token can be created on the first start
- Behind a reverse proxy (Caddy, Nginx, Cloudflare) the server sees the client's address and HTTPS
- Data protection keys encrypted with a certificate, with older certificates kept for rotation
- The agent installs as a Windows service that starts with Windows, from a PowerShell command on **Add device** that
  downloads it from the server and checks its SHA-256
- Browser tests run against the container image, behind an HTTPS proxy and across an upgrade from the version
  before, and a release publishes its image and agent only after the deployment tests pass on it behind the proxy
- Guides for deploying, backing up, restoring and upgrading, tested on a new Linux machine, and this documentation
  as a website

**v0.6 - Remote terminal** ([#26](https://github.com/fkauanGIT/StealthDesk/issues/26)) is complete: a PowerShell
terminal on a device, from the browser, for the people allowed to use it.

- A **Terminal** action on a device opens PowerShell 7.6 running in the agent, with the history of commands, multiline
  input, and completion from the device's own PowerShell
- Questions commands ask are answered in the terminal, secrets shown as stars and kept out of the history
- A `device.terminal.use` permission checked on every command; output goes only to the browser that opened the
  session, which closes when that browser leaves
- Each visit and terminal call is traced with the user and the device, never with what is typed
- Tested end to end on Windows: the agent from the container image, through the server, from a browser

Next up: **v0.7**, remote desktop.
