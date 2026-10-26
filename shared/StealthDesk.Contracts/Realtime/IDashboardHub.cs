using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;

namespace StealthDesk.Contracts.Realtime;

/// <summary>Calls a dashboard makes on the server.</summary>
public interface IDashboardHub
{
  /// <summary>The most device ids one subscription call may carry.</summary>
  public const int MaxDevicesPerSubscription = 100;

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
}
