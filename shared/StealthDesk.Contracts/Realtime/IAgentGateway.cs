using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Terminal;

namespace StealthDesk.Contracts.Realtime;

/// <summary>Calls an agent makes on the server over the realtime connection.</summary>
public interface IAgentGateway
{
  /// <summary>Stores the signed report and marks the device online.</summary>
  Task<GatewayReply<ReportReceipt>> SubmitReport(SignedEnvelope<DeviceReport> envelope);

  /// <summary>
  /// Output of a terminal session, for the browser that opened it. The server delivers it only to that browser, and
  /// only when the session was opened on this agent's device; anything else is dropped.
  /// </summary>
  Task SendTerminalOutput(TerminalOutput output);
}

/// <summary>
/// Calls the server makes on a connected agent, each answered with a <see cref="GatewayReply{T}"/>. The cancellation
/// token isn't sent: it lets the server stop waiting for an answer.
/// </summary>
public interface IAgentCallbacks
{
  /// <summary>The version of the agent answering: a harmless call that proves the server can reach it.</summary>
  Task<GatewayReply<string>> GetAgentVersion(CancellationToken cancellationToken);

  /// <summary>
  /// Starts a terminal session with this id, which sends its banner and first prompt through
  /// <see cref="IAgentGateway.SendTerminalOutput"/> before the answer. A session already running is left as it is.
  /// </summary>
  Task<GatewayReply<Guid>> OpenTerminal(Guid terminalId, CancellationToken cancellationToken);

  /// <summary>A line typed in the terminal: the answer to a question waiting, or the next command.</summary>
  Task<GatewayReply<TerminalInputReceipt>> SendTerminalInput(Guid terminalId, string line, CancellationToken cancellationToken);

  /// <summary>Completions for what is typed in the session; refused while a command runs.</summary>
  Task<GatewayReply<TerminalCompletions>> GetTerminalCompletions(Guid terminalId, TerminalCompletionRequest request, CancellationToken cancellationToken);

  /// <summary>Ends the session, stopping what it runs; its last output says so. Refused for one that isn't running.</summary>
  Task<GatewayReply<Guid>> CloseTerminal(Guid terminalId, CancellationToken cancellationToken);
}
