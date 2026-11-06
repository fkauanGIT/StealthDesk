# Tenant settings

Each tenant can change a few settings on the **Settings** page (or `/api/v1/tenant-settings`); one it hasn't set
follows the server's default, from the `TenantDefaults` [settings](../reference/server-settings.md#agents-and-tenants).

The install commands **Add device** gives can name an agent instance (`--instance`), so agents of several servers can
share a machine. See [Installing agents](installing-agents.md).

When someone starts a remote control session of a device, the person at the device is told in a message on its
screen, and again when the session ends. Each user chooses this for themselves, on their account page (**Manage account** → Remote control); a tenant can
force it on or off for everyone under **Remote control** here, which then can't be changed per user.
