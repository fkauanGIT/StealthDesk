using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;

namespace StealthDesk.Contracts.Realtime;

/// <summary>Calls an agent makes on the server over the realtime connection.</summary>
public interface IAgentGateway
{
  /// <summary>Stores the signed report and marks the device online.</summary>
  Task<GatewayReply<ReportReceipt>> SubmitReport(SignedEnvelope<DeviceReport> envelope);
}

/// <summary>
/// Calls the server makes on a connected agent, each answered with a <see cref="GatewayReply{T}"/>. The cancellation
/// token isn't sent: it lets the server stop waiting for an answer.
/// </summary>
public interface IAgentCallbacks
{
  /// <summary>The version of the agent answering: a harmless call that proves the server can reach it.</summary>
  Task<GatewayReply<string>> GetAgentVersion(CancellationToken cancellationToken);
}
