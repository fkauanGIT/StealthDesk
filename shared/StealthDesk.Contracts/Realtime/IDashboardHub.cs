using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.Contracts.Terminal;

namespace StealthDesk.Contracts.Realtime;

/// <summary>Calls a dashboard makes on the server.</summary>
public interface IDashboardHub
{
  /// <summary>The most device ids one subscription call may carry.</summary>
  public const int MaxDevicesPerSubscription = 100;

  /// <summary>
  /// The most characters one terminal input may carry: even written in three bytes each, they fit in the 32 KB a
  /// realtime message may carry by default.
  /// </summary>
  public const int MaxTerminalInputLength = 8 * 1024;

  /// <summary>
  /// Starts sending <see cref="IDashboardCallbacks.DeviceChanged"/> for these devices. Answers the ids subscribed:
  /// those the user may read; the others are left out without saying why.
  /// </summary>
  Task<GatewayReply<IReadOnlyList<Guid>>> SubscribeToDevices(IReadOnlyList<Guid> deviceIds);

  /// <summary>Stops sending changes of these devices.</summary>
  Task UnsubscribeFromDevices(IReadOnlyList<Guid> deviceIds);

  /// <summary>
  /// The actions the user may use on this device, decided now, denies first. A device they may not read is refused
  /// the same way as one that doesn't exist.
  /// </summary>
  Task<GatewayReply<DeviceActions>> GetDeviceActions(Guid deviceId);

  /// <summary>
  /// Starts recording a visit to the device's remote features: who used it and what for, in the server's traces. Lasts
  /// until <see cref="EndDeviceAccess"/>, another visit, or the connection drops. A device the user may not read is
  /// refused as one that doesn't exist.
  /// </summary>
  Task<GatewayReply<Guid>> StartDeviceAccess(Guid deviceId);

  /// <summary>Ends the visit this connection started, if any.</summary>
  Task EndDeviceAccess();

  /// <summary>
  /// Opens a terminal on the device and answers its id. Its output comes through
  /// <see cref="IDashboardCallbacks.ReceiveTerminalOutput"/>, to this connection only, starting before the answer.
  /// </summary>
  Task<GatewayReply<Guid>> OpenTerminal(Guid deviceId);

  /// <summary>A line typed in a terminal this connection opened; the receipt says whether it answered a question.</summary>
  Task<GatewayReply<TerminalInputReceipt>> SendTerminalInput(Guid terminalId, string line);

  /// <summary>Completions for what is typed in a terminal this connection opened: Tab, Shift+Tab, or every match.</summary>
  Task<GatewayReply<TerminalCompletions>> GetTerminalCompletions(Guid terminalId, TerminalCompletionRequest request);

  /// <summary>Closes a terminal this connection opened; its last output says it ended.</summary>
  Task<GatewayReply<Guid>> CloseTerminal(Guid terminalId);

  /// <summary>
  /// The Windows sessions of the device a remote control session can show, as its agent knows them now. Needs the
  /// permission to see the device's desktop.
  /// </summary>
  Task<GatewayReply<IReadOnlyList<RemoteDesktopSession>>> GetDesktopSessions(Guid deviceId);

  /// <summary>
  /// Starts a remote control session of that Windows session: the desktop client there is told to join the relay, and
  /// the answer is what this browser joins it with. The session ends when either end leaves the relay, this connection
  /// drops, or the device goes offline.
  /// </summary>
  Task<GatewayReply<RemoteControlTicket>> StartRemoteControl(Guid deviceId, int desktopSessionId);
}
