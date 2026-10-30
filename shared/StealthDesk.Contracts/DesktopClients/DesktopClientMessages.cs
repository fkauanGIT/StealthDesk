using MessagePack;
using StealthDesk.Contracts.RemoteControl;

namespace StealthDesk.Contracts.DesktopClients;

/// <summary>What a desktop client tells its agent once their pipe is open.</summary>
/// <param name="SessionId">The Windows session the client runs in.</param>
/// <param name="Version">The client's product version, so an agent can tell a client left over from another release.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record DesktopClientHello(int SessionId, string Version);

/// <summary>The Windows session a desktop client runs in, as the client sees it.</summary>
/// <param name="SessionId">The session's id.</param>
/// <param name="UserName">Who is logged on, or null at the sign-in screen.</param>
/// <param name="IsConsole">The machine's own screen, as opposed to a remote desktop session.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record DesktopSessionInfo(int SessionId, string? UserName, bool IsConsole);

/// <summary>A remote control session the agent hands to the desktop client of the session it shows.</summary>
/// <param name="RelaySessionId">The relay session to join.</param>
/// <param name="RelayAddress">Where to join it, with the device's one-time token in it: never logged.</param>
/// <param name="ViewerName">Who asked, as the user is told.</param>
/// <param name="NotifyUser">Tell the user when the session starts and ends.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record DesktopRemoteControlStart(
  Guid RelaySessionId,
  Uri RelayAddress,
  string ViewerName,
  ViewerPermissions Permissions,
  bool NotifyUser);
