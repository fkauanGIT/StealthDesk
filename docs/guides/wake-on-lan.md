# Waking a device over the network

Wake an offline device from the browser, using Wake-on-LAN.

## Who may use it

Takes the `device.wake.send` permission on the device, on a device group it is in, or on the whole tenant. The
**Device Superuser** preset includes it. [Permissions](../explanation/permissions.md) explains how grants and
denies combine.

## How it works

The target device is offline, so it can't broadcast its own wake packet. Instead, the server looks for another
device of the same tenant that is online and shares the target's known public address, and asks that device to
broadcast the packet onto its own local network. One of them should be on the same network as the target, since a
Wake-on-LAN packet doesn't cross routers on its own.

This needs:

- The target's MAC address, already collected by its agent before it went offline
- Another device of the tenant that is online right now, on the same network as the target

Without either, waking it is refused.

## Waking a device

1. Open the device from **Devices**.
2. Choose **Wake**, at the top right. It shows only to people who may wake that device, and only while it's
   offline and has a known MAC address.

There's no confirmation to make: sending a wake packet can't do any harm, and there's nothing to undo. Whether the
device actually wakes depends on it (and its network adapter) supporting Wake-on-LAN and having it enabled.
