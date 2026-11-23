# Using the file manager

The file manager browses a device's filesystem from the browser.

## Who may use it

Browsing takes the `device.file-manager.read` permission on the device, on a device group it is in, or on the whole
tenant. Uploading takes a separate one, `device.file-manager.write`. The **Device Superuser** preset includes both.
[Permissions](../explanation/permissions.md) explains how grants and denies combine.

!!! warning "No sandboxing of where you can look"
    The permission is the only gate: once granted, it reaches anywhere the agent's own account can, same as the
    terminal. On a device where the agent runs as a service, that account is **LocalSystem**.

## Browsing

1. Open the device from **Devices**.
2. Choose **Files**, at the top right. It shows only to people who may browse that device, and while the device is
   offline it is disabled, with "Available when the device is online".

It opens at the device's system drive. Choose a folder to open it; the path above the list goes back to any folder
on the way there. A folder that no longer exists, or isn't reachable right now, says so instead of erroring.

## Downloading a file

Choose **Download** next to a file to save it from the device, streamed through the server rather than held in its
memory. The same `device.file-manager.read` permission is enough; there's no separate one for downloading.

A file bigger than the server's limit (`FileManager:MaxDownloadSizeBytes` in
[server settings](../reference/server-settings.md), 2 GiB by default) can't be downloaded at all.

## Downloading a folder as a zip

Choose **Download as zip** next to a folder to save its whole contents, zipped by the device itself before any byte
reaches the server. The zip's size counts against the same download limit as a single file; a folder that zips past
it can't be downloaded either.

## Uploading a file

Choose a file to upload it into the folder currently open, under the `device.file-manager.write` permission. It's
refused if a file of that name is already there — there's no option to overwrite one yet — and if it's bigger than
the server's upload limit (`FileManager:MaxUploadSizeBytes`, 2 GiB by default).

## Creating a folder

Choose **New folder** and name it to create an empty folder inside the one currently open, under the same
`device.file-manager.write` permission as uploading. Refused if something with that name is already there.
