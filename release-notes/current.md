# v0.5.0 — Deployment

Fifth milestone of StealthDesk: it runs on a company server, as a container behind HTTPS, and agents install as a
Windows service from a command copied from the web.

## What's in it

**Running the server**
- A container image, published to Docker Hub as `franciscokauan/stealthdesk` with each release, that ships the
  Windows agent it hands out
- Docker Compose files for the server and PostgreSQL, configured through environment variables or Docker secrets,
  with an optional telemetry dashboard
- Every setting can come from a `StealthDesk_` variable, and a wrong one stops the server at start with the setting
  named
- A first administrator, personal access token and server service account can be created on the first start
- `/health/ready` and `/health/live` for orchestrators and the image's own health check

**Security**
- Behind a reverse proxy: trusted proxies and networks, and Cloudflare's ranges, so the server sees the real client
  address and HTTPS
- Data protection keys encrypted with a certificate, with older certificates kept for rotation: sessions survive a
  restart or an upgrade
- A certificate the server may not read stops it with a message that names the file and the user to give it to

**Agents**
- The agent installs as a Windows service that starts with Windows, and uninstalls without leaving anything behind
- Add device gives one PowerShell command that downloads the agent from the server, checks its SHA-256 and installs
  it; running it again updates an installed agent
- `--server` takes any http or https address

**Quality**
- The browser tests run against the container image, behind an HTTPS proxy, and across an upgrade from the version
  before, with encrypted keys
- A release publishes its image and agent only after the deployment tests pass on that image behind an HTTPS proxy

**Documentation**
- The documentation is a website with search: [fkauangit.github.io/StealthDesk](https://fkauangit.github.io/StealthDesk/)
- Guides to deploying on a company server, behind Caddy or Nginx, backing up and restoring, and upgrading and
  rolling back, tested on a new Linux machine

**License**
- StealthDesk is source-available under the Apache License 2.0 with the Commons Clause: free to use, change and
  share, not to sell

## Getting started

To try it, [your first server](https://fkauangit.github.io/StealthDesk/getting-started/first-server/): copy
`.env.example` to `.env`, set the database password, run `docker compose up -d` and open `http://localhost:8080`. For
a company server, follow [Deploying on a company server](https://fkauangit.github.io/StealthDesk/guides/deploying/).

## Known limitations

- The agent build isn't code-signed: Windows SmartScreen may warn about it
- Images are built for `linux/amd64` only

**Next:** v0.6.
