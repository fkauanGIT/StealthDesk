using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Contracts.Terminal;
using StealthDesk.Web.Server.Dashboard;

namespace StealthDesk.Web.Server.Terminal;

/// <summary>A terminal session the server relays: the device it runs on and the browser connection that opened it.</summary>
public sealed record TerminalLink(Guid TerminalId, Guid DeviceId, string ViewerConnectionId);

/// <summary>
/// The terminal sessions open through this server. An agent only says which session its output belongs to; the
/// server decides where it goes, so an agent can't reach a browser that didn't open a session on its device.
/// </summary>
public interface ITerminalRelay
{
  void Add(TerminalLink link);

  /// <summary>The session, if <paramref name="viewerConnectionId"/> opened it.</summary>
  TerminalLink? Find(Guid terminalId, string viewerConnectionId);

  void Remove(Guid terminalId);

  /// <summary>Lets go of every session the connection opened and answers them, e.g. to close them on their devices.</summary>
  IReadOnlyList<TerminalLink> RemoveAll(string viewerConnectionId);

  /// <summary>Sends the output to the browser that opened its session, when that session runs on <paramref name="deviceId"/>.</summary>
  Task DeliverAsync(Guid deviceId, TerminalOutput output);
}

public sealed class TerminalRelay(
  IHubContext<DashboardHub, IDashboardCallbacks> dashboards,
  ILogger<TerminalRelay> logger) : ITerminalRelay
{
  // Kept in memory: a session lives in a browser connection and an agent process, neither of which outlives a restart.
  private readonly ConcurrentDictionary<Guid, TerminalLink> _links = new();

  public void Add(TerminalLink link) => _links[link.TerminalId] = link;

  public TerminalLink? Find(Guid terminalId, string viewerConnectionId) =>
    _links.TryGetValue(terminalId, out var link) && link.ViewerConnectionId == viewerConnectionId ? link : null;

  public void Remove(Guid terminalId) => _links.TryRemove(terminalId, out _);

  public IReadOnlyList<TerminalLink> RemoveAll(string viewerConnectionId) =>
    [.. _links.Values.Where(x => x.ViewerConnectionId == viewerConnectionId && _links.TryRemove(new(x.TerminalId, x)))];

  public async Task DeliverAsync(Guid deviceId, TerminalOutput output)
  {
    if (!_links.TryGetValue(output.TerminalId, out var link) || link.DeviceId != deviceId)
    {
      // The last words of a session the browser already left are expected; anything else is an agent out of line.
      logger.Log(
        output.Kind == TerminalOutputKind.Ended ? LogLevel.Debug : LogLevel.Warning,
        "Dropped output of terminal {TerminalId} from device {DeviceId}: no browser opened it there.",
        output.TerminalId,
        deviceId);
      return;
    }

    if (output.Kind == TerminalOutputKind.Ended)
    {
      _links.TryRemove(new(link.TerminalId, link));
    }

    await dashboards.Clients.Client(link.ViewerConnectionId).ReceiveTerminalOutput(output);
  }
}
