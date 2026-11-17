# vX.Y.Z — Name

What the next release brings, written as each change lands. The heading becomes the release's title: before
tagging, give it the version, which has to match the tag and Directory.Build.props.

- A file manager: browse a device's filesystem from the browser, under a new `device.file-manager.read` permission.
  It opens at the device's system drive and can go into any folder the agent's own account can reach
- Download a file from the file manager, streamed from the device to the browser in chunks rather than held in
  memory. A limit on how large a file may be, `FileManager:MaxDownloadSizeBytes` (2 GiB by default), refuses a
  bigger one before any of it is sent
