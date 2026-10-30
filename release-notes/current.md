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

**Installing agents**
- The server hands out a zip of the agent and its desktop client (`StealthDesk.Agent.zip`) instead of the agent alone;
  the install command checks the zip's SHA-256 before unpacking anything, and releases attach the zip

## Upgrading

Nothing to do beyond the usual [upgrade](https://fkauangit.github.io/StealthDesk/guides/upgrading/): a migration adds
the remote control permissions. A proxy with its own sign-in in front of the server must leave `/relay` open, like
`/hubs/agent`. Agents installed before take the desktop client when installed again with a new install command.
