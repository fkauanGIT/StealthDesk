# Upgrading and rolling back

Each [release](https://github.com/fkauanGIT/StealthDesk/releases) publishes the server image as
`franciscokauan/stealthdesk` with its version, and its notes say what changed. A server pinned with
`SERVER_IMAGE_TAG` stays on its version until you change it.

## Upgrade

1. Read the release notes of every version between yours and the new one.
2. [Back up](backup-and-restore.md#back-up-the-database) the database: it is the way back.
3. In `docker-compose/.env`, set the new version, then start it:

    ```bash
    # SERVER_IMAGE_TAG=0.6.0 in .env
    docker compose pull server
    docker compose up -d --wait
    ```

The server brings the database up to date as it starts, and becomes healthy once it is done. Signed-in people stay
signed in and agents reconnect on their own: the [data protection keys](encrypting-keys.md) and the devices' keys are
in the database, kept across the upgrade.

To bring the database up to date another way, such as a reviewed SQL script, set `ApplyMigrationsOnStart` to
`false`: the server then refuses to start until the database is up to date.

## Roll back

An upgrade may change the database in ways the version before doesn't know. Going back is therefore a
[restore](backup-and-restore.md#restore) of the backup made before upgrading, with the version it was made with:

1. Set `SERVER_IMAGE_TAG` back to the version before.
2. Restore the backup: stop the server, `pg_restore`, then `docker compose up -d --wait`.

What changed after the upgrade, such as new devices or users, is lost: agents that joined since need their install
command again.

## Agents

To update an agent, run its install command again from **Add device**: it downloads the agent the server hands out,
which comes with the server's image, replaces the installed one in place and keeps the device. The release notes say
when a version needs its agents updated.
