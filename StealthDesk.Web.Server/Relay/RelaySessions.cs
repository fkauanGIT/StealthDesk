using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StealthDesk.Contracts.Relay;

namespace StealthDesk.Web.Server.Relay;

/// <summary>What each end of a relay session is given to join it: the session and its own one-time token.</summary>
public sealed record RelayTickets(Guid SessionId, string ViewerToken, string DeviceToken);

/// <summary>
/// The relay sessions the server issued. The server decides who may connect and issues a session for them; the relay
/// only pairs the two ends that present its tokens.
/// </summary>
public interface IRelaySessions
{
  /// <summary>A new session whose viewer end must be <paramref name="viewerUserId"/>, signed in.</summary>
  RelayTickets Issue(Guid viewerUserId);
}

public sealed class RelaySessions(TimeProvider clock, IOptions<RelayOptions> options) : IRelaySessions
{
  // Kept in memory: a session lives in two connections to this server, which don't outlive it.
  private readonly ConcurrentDictionary<Guid, RelaySession> _sessions = new();

  public RelayTickets Issue(Guid viewerUserId)
  {
    ForgetUnused();
    var session = new RelaySession(Guid.NewGuid(), viewerUserId, Token(), Token(), clock.GetUtcNow());
    _sessions[session.Id] = session;
    return new RelayTickets(session.Id, session.ViewerToken, session.DeviceToken);
  }

  /// <summary>The session, unless it doesn't exist or nobody joined it in time.</summary>
  internal RelaySession? Find(Guid sessionId)
  {
    if (!_sessions.TryGetValue(sessionId, out var session))
    {
      return null;
    }

    if (IsUnused(session))
    {
      Forget(session);
      return null;
    }

    return session;
  }

  internal void Forget(RelaySession session) => _sessions.TryRemove(new(session.Id, session));

  private bool IsUnused(RelaySession session) =>
    !session.AnyoneJoined && clock.GetUtcNow() - session.IssuedAt > options.Value.TicketLifetime;

  private void ForgetUnused()
  {
    foreach (var session in _sessions.Values.Where(IsUnused))
    {
      Forget(session);
    }
  }

  private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>A session's two ends, each joining once with its own token.</summary>
internal sealed class RelaySession(Guid id, Guid viewerUserId, string viewerToken, string deviceToken, DateTimeOffset issuedAt)
{
  private readonly TaskCompletionSource _viewerArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private readonly TaskCompletionSource _deviceArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private readonly TaskCompletionSource<RelayEnd> _viewer = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private readonly TaskCompletionSource<RelayEnd> _device = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private int _viewerJoined;
  private int _deviceJoined;
  private int _ended;

  public Guid Id { get; } = id;

  public Guid ViewerUserId { get; } = viewerUserId;

  public string ViewerToken { get; } = viewerToken;

  public string DeviceToken { get; } = deviceToken;

  public DateTimeOffset IssuedAt { get; } = issuedAt;

  public bool AnyoneJoined => Volatile.Read(ref _viewerJoined) == 1 || Volatile.Read(ref _deviceJoined) == 1;

  /// <summary>Cancelled once the session ends, whichever end ends it.</summary>
  public CancellationTokenSource Ended { get; } = new();

  public bool Accepts(RelayRole role, string token) => CryptographicOperations.FixedTimeEquals(
    Encoding.UTF8.GetBytes(role == RelayRole.Viewer ? ViewerToken : DeviceToken),
    Encoding.UTF8.GetBytes(token));

  /// <summary>Takes the end's place; false when that end already joined: a token is good once.</summary>
  public bool Join(RelayRole role)
  {
    var joined = Interlocked.Exchange(ref (role == RelayRole.Viewer ? ref _viewerJoined : ref _deviceJoined), 1) == 0;
    if (joined)
    {
      (role == RelayRole.Viewer ? _viewerArrived : _deviceArrived).TrySetResult();
    }

    return joined;
  }

  public Task Arrived(RelayRole role) => (role == RelayRole.Viewer ? _viewerArrived : _deviceArrived).Task;

  public void Connected(RelayRole role, RelayEnd end) => (role == RelayRole.Viewer ? _viewer : _device).TrySetResult(end);

  public Task<RelayEnd> End(RelayRole role) => (role == RelayRole.Viewer ? _viewer : _device).Task;

  /// <summary>True the first time only: whoever ends the session closes both ends.</summary>
  public bool TryEnd() => Interlocked.Exchange(ref _ended, 1) == 0;
}

/// <summary>A connected end; what is sent to it goes one at a time, as a WebSocket requires.</summary>
internal sealed class RelayEnd(WebSocket socket)
{
  private readonly SemaphoreSlim _sending = new(1, 1);

  public WebSocket Socket { get; } = socket;

  public async Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, bool endOfMessage, CancellationToken cancellationToken)
  {
    await _sending.WaitAsync(cancellationToken);
    try
    {
      await Socket.SendAsync(data, type, endOfMessage, cancellationToken);
    }
    finally
    {
      _sending.Release();
    }
  }

  /// <summary>Closes the end with the reason, unless it is gone already.</summary>
  public async Task CloseAsync(WebSocketCloseStatus status, string reason)
  {
    await _sending.WaitAsync();
    try
    {
      if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
      {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Socket.CloseOutputAsync(status, reason, limit.Token);
      }
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
    {
      // Already gone: nothing to tell it.
    }
    finally
    {
      _sending.Release();
    }
  }
}
