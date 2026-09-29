using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;

namespace StealthDesk.Contracts.Realtime;

/// <summary>Calls an agent makes on the server over the realtime connection.</summary>
public interface IAgentGateway
{
  /// <summary>Stores the signed report and marks the device online.</summary>
  Task<GatewayReply<ReportReceipt>> SubmitReport(SignedEnvelope<DeviceReport> envelope);
}

/// <summary>Calls the server makes on a connected agent. None yet.</summary>
public interface IAgentCallbacks
{
}
