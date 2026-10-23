# StealthDesk documentation

StealthDesk is a self-hosted remote device management platform built with .NET 10 and ASP.NET Core. A lightweight
agent runs on each managed machine and keeps a signed, real-time SignalR connection to the server, which tracks every
device and, in later versions, lets technicians reach them from the browser.

The pages are grouped by what you came to do.

## Getting started

Step by step, from nothing to a device in the list.

- [Your first server](getting-started/first-server.md): run the server from source and create the first account
- [Your first agent](getting-started/first-agent.md): join a machine with an installer key and watch it come online

## Guides

One task each, for a server you already know how to start.

- [Deploying on a company server](guides/deploying.md): from nothing to HTTPS, agents and backups, step by step
- [Running with Docker Compose](guides/docker-compose.md): the server and PostgreSQL in containers
- [Behind a reverse proxy](guides/reverse-proxy.md): HTTPS in front of the server
- [Encrypting the data protection keys](guides/encrypting-keys.md): with a certificate, and rotating it
- [Installing agents](guides/installing-agents.md): as a Windows service, from the command Add device gives
- [Backing up and restoring](guides/backup-and-restore.md): the database, the key certificate, and a restore drill
- [Upgrading and rolling back](guides/upgrading.md): to a new version, and back
- [Accounts and sign-in](guides/accounts.md): registration, the first administrator, Microsoft and GitHub sign-in
- [Email](guides/email.md): sending account emails by SMTP
- [Tenant settings](guides/tenant-settings.md): what each tenant can change
- [Running the tests](guides/running-the-tests.md): the test projects and the browser tests
- [Releasing a version](guides/releasing.md): what a release publishes and how to cut one

## Reference

Everything there is, to look up.

- [Server settings](reference/server-settings.md): every setting, its default and how to set it
- [Endpoints](reference/endpoints.md): the REST API, the hubs and the health checks
- [Agent command line](reference/agent-command-line.md): commands, options and where the agent keeps its files

## Explanation

Why StealthDesk works the way it does.

- [Architecture](explanation/architecture.md): the server, the agent, the web client and the code that holds them
- [Permissions](explanation/permissions.md): who may do what, and why a deny always wins
- [How agents are trusted](explanation/agent-trust.md): installer keys, signed reports and the device's identity

[Project status and roadmap](project-status.md) lists what each version brought and what comes next.
