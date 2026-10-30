using MessagePack;

namespace StealthDesk.Contracts.DesktopClients;

/// <summary>What a desktop client tells its agent once their pipe is open.</summary>
/// <param name="SessionId">The Windows session the client runs in.</param>
/// <param name="Version">The client's product version, so an agent can tell a client left over from another release.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record DesktopClientHello(int SessionId, string Version);

/// <summary>The Windows session a desktop client runs in, as the client sees it.</summary>
/// <param name="SessionId">The session's id.</param>
/// <param name="UserName">Who is logged on, or null at the sign-in screen.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record DesktopSessionInfo(int SessionId, string? UserName);
