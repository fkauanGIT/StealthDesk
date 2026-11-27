# Restarting and shutting down a device

Restart or shut down a device from the browser, without touching it in person.

## Who may use it

Takes the `device.power.manage` permission on the device, on a device group it is in, or on the whole tenant. The
**Device Superuser** preset includes it. [Permissions](../explanation/permissions.md) explains how grants and
denies combine.

## Restarting or shutting down

1. Open the device from **Devices**.
2. Choose **Restart** or **Shutdown**, at the top right. They show only to people who may manage that device's
   power, and only while it is online.
3. Confirm. The command goes to the agent right away; what happens next is between the agent and the operating
   system, the same as running the command at the machine itself.

There's no delayed or scheduled shutdown, and no way to cancel one already sent.
