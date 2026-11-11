# v0.8.0 — Remote desktop extras

## What's in it

**Remote control**
- New permission, `device.remote-control.block-input`: a viewer who holds it can block and unblock the device's own
  mouse and keyboard during a session, so only the viewer drives the desktop. The device's own input works again as
  soon as the blocking is turned off or the session ends
- A privacy screen on the device, under `device.remote-control.interact`: blanks the device's own displays while the
  viewer keeps seeing the real desktop. Lifts automatically whenever the session ends
- The viewer is told when the device's own Windows session locks, unlocks, switches to another user, or signs out,
  beyond the secure-screen message a lock already triggered. No extra permission needed
- A still preview of the device's desktop before connecting, under a new `device.desktop-preview.read` permission,
  on the device's page; the server can turn the feature off entirely with `RemoteControl:EnableDesktopPreview`
- The toolbar shows how the capture is doing — frames per second, outgoing bandwidth and round-trip latency — once a
  second for as long as the session is open
- A per-user preference for the "a session started/ended" notification shown on the device, under **Remote
  control** on the account page; a tenant that sets its own default still overrides it
- A video mode for remote control: the viewer may choose a continuous video stream instead of still regions, which
  costs less bandwidth for fast-changing content such as video playing on the device. Regions stay the default.
  Decoded with the browser's own WebCodecs API for low latency, not a buffered media container

**Docs**
- A security model page consolidating how StealthDesk handles transport, sign-in, personal access tokens and
  service account keys, authorization, agent trust, and what remote control and the terminal never record

## Upgrading

Nothing to do beyond the usual [upgrade](https://fkauangit.github.io/StealthDesk/guides/upgrading/): a migration
adds the new permissions (`device.remote-control.block-input`, `device.desktop-preview.read`) and the account's
remote control notification preference, each backfilled to a safe default for existing accounts and devices.
