# Architecture

StealthDesk has three parts: a server, an agent on each managed machine, and a web client in the browser.

## The agent and the server

The agent runs on Windows. It collects device information (name, CPU, memory, drives, IP and MAC addresses,
logged-in users) and connects to the SignalR hub at `/hubs/agent`. It retries with backoff and jitter, and sends a
heartbeat every 5 minutes (10 seconds in debug). Every heartbeat is signed with the device's own key; the server
verifies each signature, rejects stale messages and key changes, and saves the device.
[How agents are trusted](agent-trust.md) explains the keys.

Devices are marked offline when their agent disconnects, and when the server starts. Agents the server forgot
register again on their own.

## The web client

The web client is a Blazor WebAssembly application served by the server. It shows every device and keeps it current
without refreshing, through a second SignalR hub for browsers at `/hubs/dashboard`: a device turns online or offline
on the page within seconds, and its details update live. The page shows whether it is live or reconnecting, and
reloads the list after reconnecting.

The app is organized by task: Devices, People & access, Integrations, Activity and Server. What each user sees and
may do there follows their [permissions](permissions.md).

## The repository

| Path | Contents |
|---|---|
| `StealthDesk.Web.Client/` | Blazor WebAssembly front end. `Ui/` holds the design system components and `wwwroot/css/tokens.css` its colors, type and spacing |
| `StealthDesk.Web.Server/` | ASP.NET Core server with the agent gateway, REST API and EF Core database |
| `StealthDesk.Agent/` | The agent's console executable, with `StealthDesk.Agent.Core/` (settings, identity, connection, heartbeat) and `StealthDesk.Agent.Windows/` (device inventory through Windows APIs) next to it |
| `shared/` | Used by both sides: `Contracts` (messages and the gateway interface), `Core` (message signing, retry backoff), `Realtime` (typed SignalR channel), `Hosting` (file logging), `Observability` (health checks, OpenTelemetry) and `Branding` |
| `tests/` | `StealthDesk.Shared.Tests`, `StealthDesk.Server.Tests`, `StealthDesk.Agent.Tests`, `StealthDesk.Web.Client.Tests`, and the browser tests in `StealthDesk.Web.UITests` |
| `docker-compose/` | Compose files that run the server with PostgreSQL, from variables or from Docker secrets |
| `docs/` | This documentation |
| `release-notes/` | The notes of the next release, and of each one published |
