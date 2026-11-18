# v1.0.0 — First stable release

## What's in it

**Stability**
- Unblocking a device's input during remote control now always works: every `BlockInput` call runs on one
  dedicated thread, since Windows only lets the thread that blocked input unblock it again
- A second remote control session on the same device, in the same desktop client process, keeps reporting session
  changes (lock, unlock, switch user, sign off) instead of silently stopping after the first
- The video mode's black screen on some hardware couldn't be reproduced once the agent runs as an installed
  service; ffmpeg's own stderr is now logged when it exits unexpectedly, for the next time it happens

**File manager** (the start of it — more is coming)
- Browse a device's filesystem from the browser, under a new `device.file-manager.read` permission. It opens at the
  device's system drive and can go into any folder the agent's own account can reach
- Download a file from the file manager, streamed from the device to the browser in chunks rather than held in
  memory. A limit on how large a file may be, `FileManager:MaxDownloadSizeBytes` (2 GiB by default), refuses a
  bigger one before any of it is sent
