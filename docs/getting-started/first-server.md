# Your first server

This page runs the server from its source code on your machine and creates the first account. To run it in containers
instead, see [Running with Docker Compose](../guides/docker-compose.md).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [PostgreSQL](https://www.postgresql.org/download/) (optional: the server can run with an in-memory database)
- [Docker](https://www.docker.com/products/docker-desktop/) to run the server tests that use PostgreSQL
- [dotnet-ef](https://learn.microsoft.com/en-us/ef/core/cli/dotnet), only to create new migrations:
  `dotnet tool install --global dotnet-ef`

## Start the server

### With an in-memory database

The fastest way to start the server. Data is lost when it stops.

```
git clone https://github.com/fkauanGIT/StealthDesk.git
cd StealthDesk
dotnet run --project StealthDesk.Web.Server --launch-profile http -- --UseInMemoryDatabase=true
```

The server listens on `http://localhost:5099`. Running `curl http://localhost:5099/health` should
return `Healthy`.

### With PostgreSQL

The connection is built from the `POSTGRES_*` [settings](../reference/server-settings.md).
[`appsettings.Development.json`](../../StealthDesk.Web.Server/appsettings.Development.json) points them at
`localhost:5432`, database `stealthdesk`, user `postgres` and password `password`. Change them to match your
PostgreSQL instance, or override them without editing the file through environment variables
(`StealthDesk_POSTGRES_PASSWORD`) or command-line arguments (`--POSTGRES_PASSWORD=...`). Then start the server. It
applies the pending migrations on startup, creating the database if needed:

```
dotnet run --project StealthDesk.Web.Server --launch-profile http
```

## Create the first account

A new server has no users and no tenants. Open `http://localhost:5099`, choose **Create an account** on the sign-in
page and register. You become the server administrator and land on the device list. After that, registration is
closed unless public registration is on; [Accounts and sign-in](../guides/accounts.md) explains the rules.

Next: [your first agent](first-agent.md).
