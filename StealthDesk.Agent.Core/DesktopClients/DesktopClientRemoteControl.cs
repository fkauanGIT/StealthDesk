using Microsoft.Extensions.Logging;
using StealthDesk.Agent.Core.Settings;
using StealthDesk.Contracts.DesktopClients;
using StealthDesk.Contracts.Relay;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.Core;

namespace StealthDesk.Agent.Core.DesktopClients;

/// <summary>
/// Remote control of this device's sessions: the desktop client in each one does the work, told over its pipe.
/// </summary>
public sealed class DesktopClientRemoteControl(
  DesktopClientConnections clients,
  ISettingsStore settings,
  ILogger<DesktopClientRemoteControl> logger)
{
  /// <summary>How long a desktop client gets to answer.</summary>
  public static readonly TimeSpan AnswerTimeLimit = TimeSpan.FromSeconds(5);

  /// <summary>The sessions whose desktop client answers now; one that doesn't couldn't take a session either.</summary>
  public async Task<IReadOnlyList<RemoteDesktopSession>> SessionsAsync(CancellationToken cancellationToken)
  {
    var answers = await Task.WhenAll(clients.All.Select(client =>
      client.Pipe.Ask((remote, token) => remote.GetSessionInfo(token), AnswerTimeLimit, cancellationToken)));
    return
    [
      .. answers
        .Where(x => x.Answered)
        .Select(x => x.Value!)
        .DistinctBy(x => x.SessionId)
        .OrderBy(x => x.SessionId)
        .Select(x => new RemoteDesktopSession(x.SessionId, x.UserName, x.IsConsole)),
    ];
  }

  /// <summary>
  /// Hands the session to the desktop client of its Windows session, with the relay's address built from the server
  /// address this agent uses; answers once the client took it.
  /// </summary>
  public async Task<Outcome<Guid>> StartAsync(RemoteControlRequest request, CancellationToken cancellationToken)
  {
    if (settings.Current.ServerUrl is not { } server)
    {
      return Outcome.Failure<Guid>("The agent has no server address.");
    }

    // A client that didn't say which session it runs in yet can't be told apart from the others.
    var client = clients.All.FirstOrDefault(x => x.Hello?.SessionId == request.DesktopSessionId);
    if (client is null)
    {
      return Outcome.Failure<Guid>($"No desktop client is running in session {request.DesktopSessionId}.");
    }

    var start = new DesktopRemoteControlStart(
      request.RelaySessionId,
      RelayConnection.For(server, request.RelaySessionId, RelayRole.Device, request.DeviceToken),
      request.ViewerName,
      request.Permissions,
      request.NotifyUser);
    if (await client.Pipe.Send((remote, token) => remote.StartRemoteControl(start, token), AnswerTimeLimit, cancellationToken) is { } refusal)
    {
      logger.LogWarning(
        "The desktop client of session {DesktopSessionId} didn't take remote control session {SessionId}: {Refusal}",
        request.DesktopSessionId,
        request.RelaySessionId,
        refusal);
      return Outcome.Failure<Guid>($"The desktop client of session {request.DesktopSessionId} didn't start the session: {refusal}");
    }

    // Never the address: it carries the device's token.
    logger.LogInformation(
      "Remote control session {SessionId} for {Viewer} handed to the desktop client of session {DesktopSessionId}.",
      request.RelaySessionId,
      request.ViewerName,
      request.DesktopSessionId);
    return request.RelaySessionId;
  }
}
