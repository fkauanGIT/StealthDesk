namespace StealthDesk.Contracts.Relay;

/// <summary>Which end of a relay session a connection is.</summary>
public enum RelayRole
{
  /// <summary>The browser of the user the session was issued to.</summary>
  Viewer,

  /// <summary>The desktop client on the device.</summary>
  Device,
}

/// <summary>
/// How an end joins a relay session: a WebSocket to <see cref="Routes.Relay"/> with the session, its role and the
/// one-time token the server gave that end.
/// </summary>
public static class RelayConnection
{
  public const string SessionIdQuery = "sessionId";
  public const string RoleQuery = "role";
  public const string TokenQuery = "token";

  /// <summary>The relay address of <paramref name="role"/> in <paramref name="sessionId"/>, under the server's address.</summary>
  public static Uri For(Uri server, Guid sessionId, RelayRole role, string token)
  {
    var socket = new UriBuilder(new Uri(server, Routes.Relay))
    {
      Scheme = server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
      Query = $"{SessionIdQuery}={sessionId}&{RoleQuery}={role}&{TokenQuery}={Uri.EscapeDataString(token)}",
    };
    return socket.Uri;
  }
}
