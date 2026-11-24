# vX.Y.Z — Name

What the next release brings, written as each change lands. The heading becomes the release's title: before
tagging, give it the version, which has to match the tag and Directory.Build.props.

**File manager**
- Download a whole folder from the file manager as a zip, built on the device before any byte reaches the server.
  Its size counts against the same `FileManager:MaxDownloadSizeBytes` limit as a single file
- Upload a file to the device's current folder, under a new `device.file-manager.write` permission. Streamed to the
  agent in chunks rather than buffered whole, under a separate `FileManager:MaxUploadSizeBytes` limit
- Create a new, empty folder inside the one currently open, under the same `device.file-manager.write` permission
- Delete a file or folder (with its contents), under the same `device.file-manager.write` permission, with a
  confirmation first
