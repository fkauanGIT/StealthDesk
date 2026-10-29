using MessagePack;

namespace StealthDesk.Contracts.Devices;

/// <summary>
/// What the signed-in user may do on one device they may read, so the web client shows only the actions they can
/// use. The server checks each action again when it is used.
/// </summary>
/// <param name="SeeDesktop">Start remote control and see the desktop.</param>
/// <param name="ControlDesktop">Use the mouse and keyboard during remote control.</param>
/// <param name="UseSecureScreens">See and use UAC prompts and the lock screen.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record DeviceActions(
  bool UseTerminal,
  bool SeeDesktop = false,
  bool ControlDesktop = false,
  bool UseSecureScreens = false,
  bool SendCtrlAltDel = false,
  bool ReadClipboard = false,
  bool WriteClipboard = false);
