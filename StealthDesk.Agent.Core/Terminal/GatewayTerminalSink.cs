using StealthDesk.Contracts.Realtime;
using StealthDesk.Contracts.Terminal;
using StealthDesk.Realtime;

namespace StealthDesk.Agent.Core.Terminal;

/// <summary>
/// Sends terminal output to the server over the agent's connection; the server knows which browser opened the
/// session. Waits for each piece to be taken, so pieces arrive in order and a slow browser slows the session down
/// instead of piling up its output.
/// </summary>
public sealed class GatewayTerminalSink(IRealtimeChannel<IAgentGateway> channel) : ITerminalOutputSink
{
  public Task SendAsync(TerminalOutput output, CancellationToken cancellationToken) =>
    channel.Server.SendTerminalOutput(output);
}
