# Backing up and restoring

A StealthDesk server keeps everything in its PostgreSQL database: tenants, users, devices, permissions and the data
protection keys that sign people in. Three things together bring a server back:

| What | Where | Why |
|---|---|---|
| The database | the `stealthdesk-database` volume | Everything StealthDesk knows. |
| The key certificate and its password | `docker-compose/certificates`, `.env` or the secrets | Without them the [encrypted keys](encrypting-keys.md) in the database can't be read, and everyone signs in again. |
| `.env` (or `secrets/`) | `docker-compose/` | The database password and every setting. |

Keep the certificate and its password **apart** from the database backups: together, they let anyone who has them
sign in as any user.

The commands below run in `docker-compose/`, with the stack started as in
[Deploying on a company server](deploying.md). They use the defaults: user `postgres`, database `stealthdesk`.

## Back up the database

`pg_dump` copies the database while the server runs, in PostgreSQL's compressed format:

```bash
docker compose exec -T database pg_dump -U postgres -d stealthdesk -Fc > stealthdesk-$(date +%F).dump
```

On Windows, in PowerShell, write the file from inside the container, so PowerShell doesn't re-encode its bytes:

```powershell
docker compose exec database pg_dump -U postgres -d stealthdesk -Fc -f /tmp/stealthdesk.dump
docker compose cp database:/tmp/stealthdesk.dump "stealthdesk-$(Get-Date -Format yyyy-MM-dd).dump"
```

Run it on a schedule, cron on Linux or Task Scheduler on Windows, and copy the files off the server. A dump of a new
server is about 50 kilobytes; it grows with the devices and the authorization log.

## Restore

A restore replaces the database with the backup. Stop the server first, so nothing writes while it runs:

```bash
docker compose stop server
docker compose exec -T database pg_restore -U postgres -d stealthdesk --clean --if-exists < stealthdesk.dump
docker compose start server
```

On Windows, copy the file in and restore it from there:

```powershell
docker compose stop server
docker compose cp stealthdesk.dump database:/tmp/stealthdesk.dump
docker compose exec database pg_restore -U postgres -d stealthdesk --clean --if-exists /tmp/stealthdesk.dump
docker compose start server
```

On a new machine, first put back `.env` and the certificate, start only the database
(`docker compose up -d --wait database`), restore, then start everything (`docker compose up -d --wait`). Restore
into the same version of StealthDesk the backup was made with; [upgrade](upgrading.md) after.

## Rehearse a restore

A backup is only as good as the last restore that worked. Once, and after changing how backups are made:

1. Make a backup, and note something you can check: a device's name, a user, the time of the backup.
2. Change something after it: add a user, or rename a device group.
3. Restore the backup, as above.
4. Sign in with an account from before the backup, without signing in again if your browser kept its session. Check
   the noted data is there and the change from step 2 is gone.
5. Check an agent comes back online on its own: devices reconnect to the restored server without being reinstalled.
