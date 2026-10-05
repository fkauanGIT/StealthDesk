using Microsoft.AspNetCore.SignalR;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Web.Server.Devices;

namespace StealthDesk.Web.Server.Dashboard;

/// <summary>Tells the connected dashboards that a device changed.</summary>
public interface IDeviceNotifier
{
  Task DeviceChangedAsync(DeviceRecord device);
}

public sealed class DeviceNotifier(
  IHubContext<DashboardHub, IDashboardCallbacks> dashboards,
  ILogger<DeviceNotifier> logger) : IDeviceNotifier
{
  // The change is already saved: a dashboard that misses it catches up on its next load,
  // so a failure here must never reach the agent that caused it.
  public async Task DeviceChangedAsync(DeviceRecord device)
  {
    try
    {
      await dashboards.Clients.Group(DashboardHub.TenantGroup(device.TenantId)).DeviceChanged(DeviceEndpoints.ToSummary(device));
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to notify dashboards about device {DeviceId}.", device.Id);
    }
  }
}
