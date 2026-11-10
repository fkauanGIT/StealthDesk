# vX.Y.Z — Name

What the next release brings, written as each change lands. The heading becomes the release's title: before
tagging, give it the version, which has to match the tag and Directory.Build.props.

- New permission for remote control, `device.remote-control.block-input`: a viewer who holds it can block and
  unblock the device's own mouse and keyboard during a session, so only the viewer drives the desktop. The device's
  own input works again as soon as the blocking is turned off or the session ends.
- A video mode for remote control: the viewer may choose a continuous video stream instead of still regions, which
  costs less bandwidth for fast-changing content such as video playing on the device. Regions stay the default.
  Decoded with the browser's own WebCodecs API for low latency, not a buffered media container.
