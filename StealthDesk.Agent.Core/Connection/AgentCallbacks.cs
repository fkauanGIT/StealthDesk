using StealthDesk.Agent.Core.Inventory;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Realtime;

namespace StealthDesk.Agent.Core.Connection;

/// <summary>What the agent does when the server calls it.</summary>
public sealed class AgentCallbacks : IAgentCallbacks
{
  public Task<GatewayReply<string>> GetAgentVersion(CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Accept(PortableInventory.AgentVersion));
}
