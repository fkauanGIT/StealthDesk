# Using the file manager

The file manager browses a device's filesystem from the browser.

## Who may use it

Browsing takes the `device.file-manager.read` permission on the device, on a device group it is in, or on the whole
tenant. The **Device Superuser** preset includes it. [Permissions](../explanation/permissions.md) explains how
grants and denies combine.

!!! warning "No sandboxing of where you can look"
    The permission is the only gate: once granted, it reaches anywhere the agent's own account can, same as the
    terminal. On a device where the agent runs as a service, that account is **LocalSystem**.

## Browsing

1. Open the device from **Devices**.
2. Choose **Files**, at the top right. It shows only to people who may browse that device, and while the device is
   offline it is disabled, with "Available when the device is online".

It opens at the device's system drive. Choose a folder to open it; the path above the list goes back to any folder
on the way there. A folder that no longer exists, or isn't reachable right now, says so instead of erroring.
