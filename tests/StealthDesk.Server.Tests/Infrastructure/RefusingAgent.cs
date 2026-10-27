using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Contracts.Terminal;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>An agent that refuses every call the server makes; tests override the calls they answer.</summary>
public class RefusingAgent : IAgentCallbacks
{
  private const string NoAnswers = "This test agent has no answers.";

  public virtual Task<GatewayReply<string>> GetAgentVersion(CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<string>(NoAnswers));

  public virtual Task<GatewayReply<Guid>> OpenTerminal(Guid terminalId, CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<Guid>(NoAnswers));

  public virtual Task<GatewayReply<TerminalInputReceipt>> SendTerminalInput(Guid terminalId, string line, CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<TerminalInputReceipt>(NoAnswers));

  public virtual Task<GatewayReply<Guid>> CloseTerminal(Guid terminalId, CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<Guid>(NoAnswers));
}
