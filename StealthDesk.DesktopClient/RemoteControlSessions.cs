using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.DesktopClients;
using StealthDesk.Contracts.RemoteControl;

namespace StealthDesk.DesktopClient;

/// <summary>Opens the device's end of a relay session.</summary>
internal delegate Task<WebSocket> RelayConnector(Uri address, CancellationToken cancellationToken);

internal static class RelaySocket
{
  /// <summary>Pinged as often as the server pings its end, so a proxy doesn't cut a session whose screen is still.</summary>
  public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

  public static async Task<WebSocket> ConnectAsync(Uri address, CancellationToken cancellationToken)
  {
    var socket = new ClientWebSocket();
    socket.Options.KeepAliveInterval = KeepAlive;
    try
    {
      await socket.ConnectAsync(address, cancellationToken);
      return socket;
    }
    catch
    {
      socket.Dispose();
      throw;
    }
  }
}

/// <summary>Tells the user of this session something, without waiting for them.</summary>
internal interface IUserNotice
{
  void Show(string message);
}

/// <summary>One remote control session of this client, from the agent's request until it leaves the relay.</summary>
internal sealed class RemoteControlSession(DesktopRemoteControlStart request)
{
  public Guid Id => request.RelaySessionId;

  public string ViewerName => request.ViewerName;

  /// <summary>
  /// What the viewer may do besides seeing the desktop: input that needs more than they were given is dropped here,
  /// whatever the browser sends.
  /// </summary>
  public ViewerPermissions Permissions => request.Permissions;

  private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>Done once the session ended, whether or not it ever joined the relay.</summary>
  public Task Ended => _ended.Task;

  public void Finish() => _ended.TrySetResult();
}

/// <summary>
/// The remote control sessions of this desktop client. Each joins its relay session as the device's end, in the
/// background, and lasts until the viewer leaves, the relay ends it, or the client closes. The user is told when one
/// starts and ends unless the tenant turned that off.
/// </summary>
internal sealed class RemoteControlSessions(
  RelayConnector connect,
  IUserNotice notice,
  IHostApplicationLifetime lifetime,
  ILogger<RemoteControlSessions> logger)
{
  /// <summary>How long joining the relay may take; it answers once the viewer joined too, or gives up first.</summary>
  public static readonly TimeSpan JoinTimeLimit = TimeSpan.FromSeconds(30);

  private readonly ConcurrentDictionary<Guid, RemoteControlSession> _sessions = new();

  public IReadOnlyList<RemoteControlSession> Running => [.. _sessions.Values];

  /// <summary>Starts joining the relay; null once started, otherwise why it can't.</summary>
  public string? Start(DesktopRemoteControlStart request)
  {
    if (request.RelayAddress is not { IsAbsoluteUri: true, Scheme: "ws" or "wss" })
    {
      return "The relay address isn't a WebSocket address.";
    }

    if (lifetime.ApplicationStopping.IsCancellationRequested)
    {
      return "The desktop client is closing.";
    }

    var session = new RemoteControlSession(request);
    if (!_sessions.TryAdd(session.Id, session))
    {
      return "That session is already running.";
    }

    _ = Task.Run(() => RunAsync(session, request.RelayAddress, request.NotifyUser));
    return null;
  }

  private async Task RunAsync(RemoteControlSession session, Uri address, bool notifyUser)
  {
    var joined = false;
    try
    {
      WebSocket socket;
      using (var joining = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping))
      {
        joining.CancelAfter(JoinTimeLimit);
        socket = await connect(address, joining.Token);
      }

      using (socket)
      {
        joined = true;
        logger.LogInformation("Remote control session {SessionId} for {Viewer} started.", session.Id, session.ViewerName);
        if (notifyUser)
        {
          notice.Show($"{session.ViewerName} started remote control of this computer.");
        }

        await ReceiveUntilClosedAsync(socket, lifetime.ApplicationStopping);
      }
    }
    // Never the address in the log: it carries the session's token.
    catch (Exception ex) when (!joined)
    {
      logger.LogWarning("Remote control session {SessionId} couldn't join the relay: {Error}", session.Id, ex.Message);
    }
    catch (Exception ex)
    {
      logger.LogInformation("Remote control session {SessionId} lost the relay: {Error}", session.Id, ex.Message);
    }
    finally
    {
      _sessions.TryRemove(new KeyValuePair<Guid, RemoteControlSession>(session.Id, session));
      if (joined)
      {
        logger.LogInformation("Remote control session {SessionId} for {Viewer} ended.", session.Id, session.ViewerName);
        if (notifyUser)
        {
          notice.Show($"{session.ViewerName}'s remote control of this computer ended.");
        }
      }

      session.Finish();
    }
  }

  // Nothing comes from the viewer yet but the end of the session.
  private static async Task ReceiveUntilClosedAsync(WebSocket socket, CancellationToken stopping)
  {
    var buffer = new byte[4 * 1024];
    try
    {
      while (true)
      {
        var received = await socket.ReceiveAsync(buffer.AsMemory(), stopping);
        if (received.MessageType == WebSocketMessageType.Close)
        {
          // The relay hangs up right after its close message: the session ended normally either way.
          await TryCloseAsync(socket, WebSocketCloseStatus.NormalClosure, "The session ended.");
          return;
        }
      }
    }
    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
    {
      // The client is closing: the viewer is told the device left.
      await TryCloseAsync(socket, WebSocketCloseStatus.EndpointUnavailable, "The desktop client closed.");
    }
  }

  private static async Task TryCloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
  {
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try
    {
      await socket.CloseOutputAsync(status, reason, limit.Token);
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
    {
      // Gone already.
    }
  }
}
