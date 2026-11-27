# v1.2 — Power management

What the next release brings, written as each change lands. The heading becomes the release's title: before
tagging, give it the version, which has to match the tag and Directory.Build.props.

## What's in it

**Power management**
- Restart or shut down an online device from its page, under a new `device.power.manage` permission, with a
  confirmation first
- Wake an offline device over the network (Wake-on-LAN), under a new `device.wake.send` permission: another online
  device sharing the target's public address broadcasts the magic packet, since the target itself can't
