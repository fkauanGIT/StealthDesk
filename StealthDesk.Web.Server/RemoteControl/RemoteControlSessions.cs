using System.Collections.Concurrent;
using StealthDesk.Web.Server.Dashboard;
using StealthDesk.Web.Server.Relay;

namespace StealthDesk.Web.Server.RemoteControl;

/// <summary>A remote control session a device took: whose, of which device, and from which dashboard connection.</summary>
public sealed record RemoteControlLink(Guid RelaySessionId, Guid DeviceId, string ConnectionId, string UserId, string? UserName);

/// <summary>
/// The remote control sessions running. The relay ends a session when either end leaves it; these end it too when the
/// browser that started it disconnects or its device goes offline, and record when and why each one ended.
/// </summary>
public interface IRemoteControlSessions
{
  /// <summary>Keeps the session until its relay session ends; one that already ended is recorded as ended at once.</summary>
  void Add(RemoteControlLink link);

  void EndForConnection(string connectionId, string reason);

  void EndForDevice(Guid deviceId, string reason);
}

public sealed class RemoteControlSessions(IRelaySessions relay, ILogger<RemoteControlSessions> logger) : IRemoteControlSessions
{
  private const string LeftTheRelay = "An end left the relay.";

  private readonly ConcurrentDictionary<Guid, RunningSession> _sessions = new();

  public void Add(RemoteControlLink link)
  {
    var session = new RunningSession(link);
    _sessions[link.RelaySessionId] = session;
    // Not kept: the registration goes with the relay session, and runs at once for one that already ended.
    relay.Ended(link.RelaySessionId).Register(() => Ended(session));
    logger.LogInformation(
      "Remote control session {SessionId} started on device {DeviceId} for user {UserId}.",
      link.RelaySessionId,
      link.DeviceId,
      link.UserId);
  }

  public void EndForConnection(string connectionId, string reason) => EndWhere(x => x.ConnectionId == connectionId, reason);

  public void EndForDevice(Guid deviceId, string reason) => EndWhere(x => x.DeviceId == deviceId, reason);

  private void EndWhere(Func<RemoteControlLink, bool> match, string reason)
  {
    foreach (var session in _sessions.Values.Where(x => match(x.Link)))
    {
      // The first reason given is the one recorded.
      Interlocked.CompareExchange(ref session.Reason, reason, null);
      relay.End(session.Link.RelaySessionId);
    }
  }

  private void Ended(RunningSession session)
  {
    if (!_sessions.TryRemove(new KeyValuePair<Guid, RunningSession>(session.Link.RelaySessionId, session)))
    {
      return;
    }

    var link = session.Link;
    var reason = Volatile.Read(ref session.Reason) ?? LeftTheRelay;
    using var ended = DeviceAccessTracing.StartCall(DeviceAccessTracing.RemoteControlEnd, visit: null, link.UserId, link.UserName, link.DeviceId, terminalId: null);
    ended?.SetTag(DeviceAccessTracing.RemoteControlSessionIdTag, link.RelaySessionId.ToString());
    ended?.SetTag(DeviceAccessTracing.EndReasonTag, reason);
    logger.LogInformation("Remote control session {SessionId} on device {DeviceId} ended: {Reason}", link.RelaySessionId, link.DeviceId, reason);
  }

  private sealed class RunningSession(RemoteControlLink link)
  {
    public RemoteControlLink Link { get; } = link;

    /// <summary>Why the server ended it; none when an end left the relay.</summary>
    public string? Reason;
  }
}
