# Deploying on a company server

This guide takes a server from nothing to StealthDesk running behind HTTPS, with agents installed on the company's
machines, backups and a way to upgrade. Each step links to the page with the details.

## What you need

- **A server** that runs containers, with at least 2 GB of memory and 10 GB of free disk:
    - Linux (Ubuntu 24.04 LTS, Debian 12 or similar) with [Docker Engine](https://docs.docker.com/engine/install/)
      and its Compose plugin, version 2.24 or later (`docker compose version`).
    - or Windows with [Docker Desktop](https://www.docker.com/products/docker-desktop/), running Linux containers.
- **A name** for it, such as `desk.example.com`, that the company's machines can resolve. A certificate from
  Let's Encrypt also needs the name to be public and ports 80 and 443 to reach the server.
- **Agents** run on Windows, x64. They only need to reach the server's name over HTTPS.

## 1. Get the compose files

```bash
git clone --depth 1 -c core.longpaths=true https://github.com/fkauanGIT/StealthDesk.git
cd StealthDesk/docker-compose
```

`core.longpaths` matters on Windows only: some of the repository's paths are long, and Windows refuses paths past 260
characters unless Git is told otherwise.

## 2. Configure

Copy the example settings and set the database password. `.env` holds passwords: keep it readable only by you.

=== "Linux"

    ```bash
    cp .env.example .env
    chmod 600 .env
    openssl rand -base64 24    # a password to paste into .env
    ```

=== "Windows (PowerShell)"

    ```powershell
    Copy-Item .env.example .env
    # A password, in Windows PowerShell and in PowerShell 7
    $bytes = New-Object byte[] 18; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    [Convert]::ToBase64String($bytes)
    ```

Then edit `.env`:

```ini
StealthDesk_POSTGRES_PASSWORD=<the password>
# The version to run: change it only when upgrading.
SERVER_IMAGE_TAG=0.5.0
# Only the reverse proxy on this machine reaches the server.
SERVER_PORT=127.0.0.1:8080
```

Pinning `SERVER_IMAGE_TAG` keeps the server on the version you chose until you [upgrade](upgrading.md) on purpose.
`SERVER_PORT=127.0.0.1:8080` publishes the server on this machine only, for the proxy of step 5; leave it at `8080`
to reach the server from other machines over plain HTTP, for a first try. Every other setting is in `.env.example`,
commented out at its default, and in the [server settings](../reference/server-settings.md) reference. To keep
passwords out of `.env`, use [Docker secrets](docker-compose.md#passwords-as-docker-secrets) instead.

## 3. Encrypt the data protection keys

Without this, anyone who reads the database or a backup of it can forge a sign-in. Create a certificate named
`stealthdesk-keys.pfx` with the commands in [Encrypting the data protection keys](encrypting-keys.md#create-the-certificate),
then put it in a `certificates` folder next to `docker-compose.yml`, which the server sees as `/certificates`. The
folder isn't in the repository, so that a certificate never ends up in it: create it.

=== "Linux"

    ```bash
    mkdir certificates
    mv stealthdesk-keys.pfx certificates/
    # The container's user reads it, and nobody else.
    sudo chown 1654:1654 certificates/stealthdesk-keys.pfx
    sudo chmod 400 certificates/stealthdesk-keys.pfx
    ```

=== "Windows (PowerShell)"

    ```powershell
    New-Item -ItemType Directory certificates
    Move-Item stealthdesk-keys.pfx certificates\
    ```

Then, in `.env`:

```ini
StealthDesk_KeyProtection__EncryptKeys=true
StealthDesk_KeyProtection__Certificate__Path=/certificates/stealthdesk-keys.pfx
StealthDesk_KeyProtection__Certificate__Password=<the certificate's password>
```

## 4. Start

```bash
docker compose up -d --wait
docker compose ps
```

`--wait` returns once both containers are healthy: the server answers and reaches its database. The first start
downloads the images and creates the database. Both containers restart on their own after a reboot.

## 5. Put HTTPS in front

Install a reverse proxy on the same machine and point it at `127.0.0.1:8080`: [Behind a reverse proxy](reverse-proxy.md)
has ready configurations for Caddy, which gets its certificate on its own, and Nginx. Open
`https://desk.example.com` once it answers.

## 6. Create the first account

On the sign-in page, choose **Create an account**. The first person to register becomes the server administrator;
registration closes after that unless it is opened in the settings ([Accounts and sign-in](accounts.md)). Do it right
after starting: until then, anyone who reaches the server can be that first person.

## 7. Install the agents

On the device list, choose **Add device**, create an installer key and copy the command. Run it in PowerShell as an
administrator on each machine: it downloads the agent from the server, checks its hash and installs it as a service.
[Installing agents](installing-agents.md) explains what it does, and how to uninstall.

## 8. Back up, and upgrade

- [Back up](backup-and-restore.md) the database, the key certificate and `.env`, and rehearse a restore once.
- [Upgrade](upgrading.md) by changing `SERVER_IMAGE_TAG`, after a backup.
