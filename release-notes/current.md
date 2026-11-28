# v1.2.0 — Power management

## What's in it

**Power management**
- Restart or shut down an online device from its page, under a new `device.power.manage` permission, with a
  confirmation first
- Wake an offline device over the network (Wake-on-LAN), under a new `device.wake.send` permission: another online
  device sharing the target's public address broadcasts the magic packet, since the target itself can't
