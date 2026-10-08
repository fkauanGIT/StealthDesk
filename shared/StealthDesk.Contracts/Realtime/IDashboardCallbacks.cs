using StealthDesk.Contracts.Devices;

namespace StealthDesk.Contracts.Realtime;

/// <summary>Calls the server makes on connected dashboards.</summary>
public interface IDashboardCallbacks
{
  /// <summary>A subscribed device was reported, came online or went offline.</summary>
  Task DeviceChanged(DeviceSummary device);
}
