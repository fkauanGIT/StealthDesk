# Email

Account emails (confirm the address, reset the password, confirm a new email) go out by SMTP, configured in the
`Email` [settings](../reference/server-settings.md#email).

The first user's email is confirmed right away, and so is everyone's while sending is disabled. A message that can't
be delivered is logged and can be asked for again; it never fails the registration or the reset. The server refuses
to start with `Accounts:RequireConfirmedEmail` on and sending disabled.

Server administrators can check the settings from the **Server** page, which sends a test message to their own address
(`/api/v1/server/test-email`).

See [a page that does not exist](no-such-page.md).
