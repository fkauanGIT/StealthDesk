using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Terminal;

namespace StealthDesk.Contracts.Realtime;

/// <summary>Calls the server makes on connected dashboards.</summary>
public interface IDashboardCallbacks
{
  /// <summary>A subscribed device was reported, came online or went offline.</summary>
  Task DeviceChanged(DeviceSummary device);

  /// <summary>Output of a terminal this connection opened, in the order the device sent it.</summary>
  Task ReceiveTerminalOutput(TerminalOutput output);
}
