using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Terminal;

namespace StealthDesk.Web.Client.Devices;

/// <summary>
/// Calls about one device over the live connection. Terminals go through it too: the server sends a terminal's output
/// only to the connection that opened it, and closes the terminal when that connection drops.
/// </summary>
public interface IDeviceHub
{
  /// <summary>Output of a terminal this connection opened.</summary>
  event Action<TerminalOutput>? TerminalOutputReceived;

  /// <summary>The connection dropped: every terminal it opened is closed on the server.</summary>
  event Action? ConnectionLost;

  Task<GatewayReply<DeviceActions>> GetDeviceActionsAsync(Guid deviceId);

  /// <summary>Starts the server's record of a visit to the device's remote features.</summary>
  Task<GatewayReply<Guid>> StartDeviceAccessAsync(Guid deviceId);

  Task EndDeviceAccessAsync();

  Task<GatewayReply<Guid>> OpenTerminalAsync(Guid deviceId);

  Task<GatewayReply<TerminalInputReceipt>> SendTerminalInputAsync(Guid terminalId, string line);

  Task<GatewayReply<TerminalCompletions>> GetTerminalCompletionsAsync(Guid terminalId, TerminalCompletionRequest request);

  Task<GatewayReply<Guid>> CloseTerminalAsync(Guid terminalId);
}
