using MessagePack;

namespace StealthDesk.Contracts.RemoteControl;

/// <summary>
/// What the viewer of a remote control session may do besides seeing the desktop, from their permissions on the device
/// when the session started. The desktop client drops whatever the viewer wasn't given, whatever the browser sends.
/// </summary>
/// <param name="Interact">Use the mouse and keyboard.</param>
/// <param name="UseSecureScreens">See and use UAC prompts and the lock screen.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record ViewerPermissions(
  bool Interact,
  bool UseSecureScreens,
  bool SendCtrlAltDel,
  bool ReadClipboard,
  bool WriteClipboard)
{
  public static ViewerPermissions ViewOnly { get; } = new(false, false, false, false, false);
}

/// <summary>A Windows session of a device with a desktop client in it: one a remote control session can show.</summary>
/// <param name="SessionId">The Windows session's id.</param>
/// <param name="UserName">Who is logged on, or none at the sign-in screen.</param>
/// <param name="IsConsole">The machine's own screen, as opposed to a remote desktop session.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record RemoteDesktopSession(int SessionId, string? UserName, bool IsConsole);

/// <summary>A remote control session the server asks an agent to start, in one of its Windows sessions.</summary>
/// <param name="RelaySessionId">The relay session the desktop client joins.</param>
/// <param name="DeviceToken">
/// The one-time token of the device's end; the agent puts it in the relay's address, built from the server address it
/// already uses. Never logged.
/// </param>
/// <param name="DesktopSessionId">The Windows session to show.</param>
/// <param name="ViewerName">Who asked, as the user at the device is told.</param>
/// <param name="NotifyUser">Tell the user at the device when the session starts and ends.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record RemoteControlRequest(
  Guid RelaySessionId,
  string DeviceToken,
  int DesktopSessionId,
  string ViewerName,
  ViewerPermissions Permissions,
  bool NotifyUser);

/// <summary>
/// What a browser joins its remote control session with: the session and the viewer's one-time token. The relay's
/// address comes from the server address the browser already uses, with <see cref="Relay.RelayConnection.For"/>.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record RemoteControlTicket(Guid RelaySessionId, string ViewerToken);
