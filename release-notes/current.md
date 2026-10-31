# vX.Y.Z — Name

What the next release brings, written as each change lands. The heading becomes the release's title: before
tagging, give it the version, which has to match the tag and Directory.Build.props.

## What's in it

**Remote desktop, on its way**
- New permissions for remote control, each its own action on a device: `device.remote-control.connect` (see the
  desktop), `.interact` (control it), `.elevated-desktop` (use secure screens), `device.ctrl-alt-del.send` and
  `device.clipboard.read` / `.write`, all part of the Device Superuser preset; upgrading gives them to whoever was
  given reading devices by the system
- A relay inside the server, at `/relay`, that remote control streams will go through instead of the hubs: two ends
  of a session the server issued, each with its own one-time token, the viewer's end its signed-in user; limits in
  `Relay:TicketLifetime`, `Relay:PartnerTimeout` and `Relay:MaxMessageBytes`
- A desktop client the agent's service keeps running in every Windows session with a screen, the sign-in screen
  included, as LocalSystem inside that session: what will see and use the desktop
- The agent and its desktop clients talk over a named pipe only LocalSystem and logged-on users may open; the agent
  keeps a connection only from the desktop client installed next to it, as Windows reports the process, signed like
  the agent when the agent is signed
- The server starts a remote control session of a device's Windows session: with `device.remote-control.connect` on
  the device, a browser lists its sessions and asks for one, and the desktop client there joins the relay, holding the
  viewer to what their permissions allowed when the session started. The session ends when either end leaves, the
  browser disconnects or the device goes offline, and its start and end are in the device's access trace
- During a session the desktop client streams the screen through the relay: the displays, then the whole screen, then
  only what changed, as JPEG regions, captured with DXGI desktop duplication from Windows 8 and GDI wherever that
  can't work, never faster than the connection carries. Quality by hand or following the bandwidth, an optional
  bandwidth cap, switching displays, the pointer's shape, and the pointer drawn in when asked; metrics every second
- The person at the device is told in a message on its screen when a session starts and ends, unless the tenant
  turns that off in its settings (**Remote control**); the server's default is `TenantDefaults:NotifyUserOnRemoteControl`
- The viewer's mouse and keyboard act on the device: moves, buttons, the wheel, keys with their modifiers and typed
  text, placed on the shown display and mapped from the browser's key names to the device's own layout. A view-only
  viewer (without `device.remote-control.interact`) injects nothing. Ctrl+Alt+Del, with `device.ctrl-alt-del.send`,
  is sent from the session's desktop client, which runs as the local system; install allows it with the
  `SoftwareSASGeneration` policy and uninstall turns it off
- A **Remote control** action on a device's page opens the desktop in the browser: it joins the relay, draws the
  screen on a canvas as regions arrive, and (with the control permission) sends the viewer's mouse and keyboard;
  view-only otherwise. A toolbar chooses the display and the quality, sends Ctrl+Alt+Del, goes full screen and ends
  the session; when the device has several Windows sessions, one is chosen first. Leaving the page ends the session

**Installing agents**
- The server hands out a zip of the agent and its desktop client (`StealthDesk.Agent.zip`) instead of the agent alone;
  the install command checks the zip's SHA-256 before unpacking anything, and releases attach the zip

## Upgrading

Nothing to do beyond the usual [upgrade](https://fkauangit.github.io/StealthDesk/guides/upgrading/): a migration adds
the remote control permissions. A proxy with its own sign-in in front of the server must leave `/relay` open, like
`/hubs/agent`. Agents installed before take the desktop client when installed again with a new install command.
