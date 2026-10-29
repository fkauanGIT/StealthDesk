<p align="center">
  <img src=".github/assets/Banner-StealthDesk.png" alt="StealthDesk" width="800" />
</p>

[![CI](https://github.com/fkauanGIT/StealthDesk/actions/workflows/ci.yml/badge.svg)](https://github.com/fkauanGIT/StealthDesk/actions/workflows/ci.yml)
[![Project board](https://img.shields.io/badge/project-board-blue?logo=github)](https://github.com/users/fkauanGIT/projects/3)

StealthDesk is a self-hosted remote device management platform built with .NET 10 and ASP.NET Core.
A lightweight agent runs on each managed machine and keeps a signed, real-time SignalR connection to
the server, which tracks every device and lets technicians reach them from the browser.

> **Early development:** until v1.0 there may be breaking changes between versions. Since v0.5 the server runs in a
> container behind HTTPS with its keys encrypted, but it has not had a security review yet.

## Project Status

v0.1 to v0.6 are complete: agents report their devices live to the browser, people sign in to their own tenant,
permissions decide what each of them can do, the server runs on a company server with agents installed as a Windows
service, and technicians run PowerShell on a device from the browser. **v0.7**, remote desktop, is next. See [what each version brought](docs/project-status.md).

| Version | Goal | Epic |
|---|---|---|
| v0.1 | Agent connectivity | [#23](https://github.com/fkauanGIT/StealthDesk/issues/23) |
| v0.2 | Live device dashboard | [#24](https://github.com/fkauanGIT/StealthDesk/issues/24) |
| v0.3 | User accounts | [#25](https://github.com/fkauanGIT/StealthDesk/issues/25) |
| v0.4 | Permissions and access | [#100](https://github.com/fkauanGIT/StealthDesk/issues/100) |
| v0.5 | Deployment | [#127](https://github.com/fkauanGIT/StealthDesk/issues/127) |
| v0.6 | Remote terminal | [#26](https://github.com/fkauanGIT/StealthDesk/issues/26) |
| v0.7 | Remote desktop (MVP) | [#27](https://github.com/fkauanGIT/StealthDesk/issues/27) |

## Quick Start

With [Docker](https://www.docker.com/products/docker-desktop/), the server and PostgreSQL in containers:

```bash
git clone https://github.com/fkauanGIT/StealthDesk.git
cd StealthDesk/docker-compose
cp .env.example .env    # set StealthDesk_POSTGRES_PASSWORD
docker compose up -d
```

Or from source with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), on an in-memory database:

```bash
dotnet run --project StealthDesk.Web.Server --launch-profile http -- --UseInMemoryDatabase=true
```

Open the server (`http://localhost:8080` with Docker, `http://localhost:5099` from source), choose
**Create an account**, then **Devices → Add device** to join a machine.
[Getting started](docs/getting-started/first-server.md) goes through it step by step.

## Documentation

The documentation is a website, with search: **[fkauangit.github.io/StealthDesk](https://fkauangit.github.io/StealthDesk/)**.
Its pages are also in [`docs/`](docs/index.md), grouped by what you came to do:

- **Getting started**: [your first server](docs/getting-started/first-server.md) and
  [your first agent](docs/getting-started/first-agent.md)
- **Guides**: [deploying on a company server](docs/guides/deploying.md), [Docker Compose](docs/guides/docker-compose.md), [reverse proxy](docs/guides/reverse-proxy.md),
  [encrypting the keys](docs/guides/encrypting-keys.md), [installing agents](docs/guides/installing-agents.md),
  [backups](docs/guides/backup-and-restore.md), [upgrading](docs/guides/upgrading.md),
  [accounts](docs/guides/accounts.md), [email](docs/guides/email.md),
  [tenant settings](docs/guides/tenant-settings.md), [running the tests](docs/guides/running-the-tests.md) and
  [releasing](docs/guides/releasing.md)
- **Reference**: [server settings](docs/reference/server-settings.md), [endpoints](docs/reference/endpoints.md) and
  the [agent command line](docs/reference/agent-command-line.md)
- **Explanation**: [architecture](docs/explanation/architecture.md), [permissions](docs/explanation/permissions.md)
  and [how agents are trusted](docs/explanation/agent-trust.md)

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
