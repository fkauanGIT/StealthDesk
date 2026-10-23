# Running with Docker Compose

The server and its web client build into one container image, which runs next to PostgreSQL. Each
[release](releasing.md) publishes it on Docker Hub as
[`franciscokauan/stealthdesk`](https://hub.docker.com/r/franciscokauan/stealthdesk).

## The container image

The image builds from the repository root:

```bash
docker build -f StealthDesk.Web.Server/Dockerfile -t stealthdesk-server .
```

It runs on the ASP.NET runtime as the image's unprivileged `app` user, listens on port 8080 and takes every
[setting](../reference/server-settings.md) as a `StealthDesk_` variable. With a PostgreSQL container next to it:

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
[reverse proxy](reverse-proxy.md) in front of it for HTTPS.

## With Docker Compose

[`docker-compose/`](https://github.com/fkauanGIT/StealthDesk/tree/main/docker-compose) runs the server and PostgreSQL together. The server waits for the database to
be healthy, both restart unless stopped, and the database lives in the `stealthdesk-database` volume. Users,
devices and the data protection keys are kept there, so removing and recreating the containers loses nothing.

```bash
cd docker-compose
cp .env.example .env    # set StealthDesk_POSTGRES_PASSWORD, and any other setting
docker compose up -d
```

`.env.example` lists every setting, commented out at its default. The server image is pinned with `SERVER_IMAGE_TAG`
(`0.5.0`, `0.5` or `latest`); a tag that can't be pulled is built from this repository. The server listens on
`SERVER_PORT` (8080) and trusts forwarded headers from the network's gateway (`DOCKER_GATEWAY`, 172.30.0.1), which a
reverse proxy on the same host comes through. To encrypt the [data protection keys](encrypting-keys.md), put the `.pfx`
in `docker-compose/certificates`: the server sees that folder, read-only, as `/certificates`.

## Passwords as Docker secrets

To keep passwords out of variables, `docker-compose.secrets.yml` reads them from Docker secrets instead: one file per
setting in `docker-compose/secrets`, named after it (`Bootstrap__AdminPassword` holds `Bootstrap:AdminPassword`).
The key certificate goes in `KeyProtection__Certificate__Base64` as base64. An empty file leaves its setting off.
After a [rotation](encrypting-keys.md#rotating-the-certificate), add the previous certificate the same way, as two more
secrets of the server: `KeyProtection__PreviousCertificates__0__Base64` and `KeyProtection__PreviousCertificates__0__Password`.

```bash
cp -r secrets.example secrets    # write the database password in secrets/POSTGRES_PASSWORD
chmod 700 secrets                # the files stay readable inside the containers, which run as other users
docker compose -f docker-compose.secrets.yml up -d
```

A single line break at the end of a file is dropped, but a Windows one (CRLF) leaves a carriage return in the value:
save the files with Unix line endings, or write them with `printf '%s' 'the-password' > secrets/POSTGRES_PASSWORD`.

## The telemetry dashboard

Both files can also start a dashboard for the server's logs, traces and metrics at `TELEMETRY_DASHBOARD_PORT`
(18888). Set `StealthDesk_OTEL_EXPORTER_OTLP_ENDPOINT=http://telemetry:18889`, then add `--profile telemetry`
to the `up` command. The dashboard asks for `TELEMETRY_DASHBOARD_TOKEN` (or the `Dashboard__Frontend__BrowserToken`
secret); left empty, it makes a new one on each start and logs a sign-in link (`docker compose logs telemetry`).
