# Using remote control

Remote control shows a device's desktop in the browser and lets you drive its mouse and keyboard, through the
[relay](../reference/endpoints.md).

## Who may use it

Seeing the desktop takes `device.remote-control.connect`, on the device, a device group it is in, or the tenant.
Controlling the mouse and keyboard also needs `device.remote-control.interact`; without it the session is view-only.
`device.remote-control.elevated-desktop`, `device.ctrl-alt-del.send`, `device.clipboard.read`,
`device.clipboard.write` and `device.remote-control.block-input` each unlock one more thing, described below. The
**Device Superuser** preset includes all of them. [Permissions](../explanation/permissions.md) explains how grants
and denies combine.

## Opening a session

1. Open the device from **Devices**.
2. Choose **Remote control**, at the top right. It shows only to people who may connect to that device, and while
   the device is offline it is disabled, with "Available when the device is online".

If the device offers more than one Windows session (its sign-in screen and a remote desktop session, say), it asks
which to show first. With one session, it connects straight to it. Once connected, the screen is drawn as it changes;
the whole screen comes first.

## The toolbar

| Button | What it does | Needs |
|---|---|---|
| A display picker | Shows another of the device's displays, only when it has more than one | — |
| A quality picker | Auto, high, medium or low; auto follows the connection's bandwidth | — |
| **Ctrl+Alt+Del** | Sends the secure attention sequence to the device's session | `device.ctrl-alt-del.send` |
| **Send clipboard** | Reads the browser's clipboard and sets the device's | `device.clipboard.write` |
| **Get clipboard** | Reads the device's clipboard into the browser's | `device.clipboard.read` |
| **Block input** / **Unblock input** | Blocks or unblocks the device's own mouse and keyboard | `device.remote-control.block-input` |
| **Privacy screen** / **Hide privacy screen** | Blanks the device's own displays, on Windows; you keep seeing the real desktop | `device.remote-control.interact` |
| **Full screen** | The desktop fills the browser window | — |
| **End session** | Leaves and returns to the device's page | — |

Each clipboard button shows only with its permission, and says what happened ("Clipboard sent to the device.",
or that the browser itself blocked reading or writing its clipboard, which it can without a user gesture or over
plain HTTP).

## Secure screens

A UAC prompt, the lock screen and Ctrl+Alt+Del's own screen all run apart from the desktop you were on. With
`device.remote-control.elevated-desktop` the session follows the device there, so it is shown and usable; without
it, a message says a secure screen is up, and the normal view comes back once the device leaves it.

## What the person at the device sees

They are told in a message on their screen when a session starts and ends, naming who is controlling their
computer, unless the tenant turned that off; see [tenant settings](tenant-settings.md).

## When the session ends

A session ends when:

- you choose **End session**, or leave the device's pages;
- either end leaves the relay, or the browser's connection to the server drops;
- the device goes offline.

Blocking the device's input and the privacy screen, if either was on, lift automatically whenever the session ends,
however it ends.

## What is recorded

Starting a session and its end are recorded in the server's traces with the user, the device and the outcome, the
same as the [terminal](terminal.md#what-is-recorded). What is on the screen, typed or in the clipboard is never
recorded.
