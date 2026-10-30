using StealthDesk.Agent.Core.DesktopClients;
using StealthDesk.Agent.Core.Inventory;
using StealthDesk.Agent.Core.Terminal;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.Contracts.Terminal;
using StealthDesk.Core;

namespace StealthDesk.Agent.Core.Connection;

/// <summary>What the agent does when the server calls it.</summary>
public sealed class AgentCallbacks(ITerminalSessions terminals, DesktopClientRemoteControl remoteControl) : IAgentCallbacks
{
  public Task<GatewayReply<string>> GetAgentVersion(CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Accept(PortableInventory.AgentVersion));

  public async Task<GatewayReply<Guid>> OpenTerminal(Guid terminalId, CancellationToken cancellationToken) =>
    Reply(await terminals.OpenAsync(terminalId, cancellationToken));

  public Task<GatewayReply<TerminalInputReceipt>> SendTerminalInput(Guid terminalId, string line, CancellationToken cancellationToken) =>
    Task.FromResult(Reply(terminals.Send(terminalId, line)));

  public Task<GatewayReply<TerminalCompletions>> GetTerminalCompletions(Guid terminalId, TerminalCompletionRequest request, CancellationToken cancellationToken) =>
    Task.FromResult(Reply(terminals.Complete(terminalId, request)));

  public Task<GatewayReply<Guid>> CloseTerminal(Guid terminalId, CancellationToken cancellationToken)
  {
    // Told apart so the server can let go of a session this agent no longer has, e.g. after a restart.
    if (!terminals.Running.Contains(terminalId))
    {
      return Task.FromResult(GatewayReply.Refuse<Guid>("The terminal session has ended."));
    }

    terminals.Close(terminalId);
    return Task.FromResult(GatewayReply.Accept(terminalId));
  }

  public async Task<GatewayReply<IReadOnlyList<RemoteDesktopSession>>> GetDesktopSessions(CancellationToken cancellationToken) =>
    GatewayReply.Accept(await remoteControl.SessionsAsync(cancellationToken));

  public async Task<GatewayReply<Guid>> StartRemoteControl(RemoteControlRequest request, CancellationToken cancellationToken) =>
    Reply(await remoteControl.StartAsync(request, cancellationToken));

  private static GatewayReply<T> Reply<T>(Outcome<T> outcome) =>
    outcome.Succeeded ? GatewayReply.Accept(outcome.Value!) : GatewayReply.Refuse<T>(outcome.Error);
}
