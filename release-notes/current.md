# vX.Y.Z — Name

What the next release brings, written as each change lands. The heading becomes the release's title: before
tagging, give it the version, which has to match the tag and Directory.Build.props.

- New permission for remote control, `device.remote-control.block-input`: a viewer who holds it can block and
  unblock the device's own mouse and keyboard during a session, so only the viewer drives the desktop. The device's
  own input works again as soon as the blocking is turned off or the session ends.
