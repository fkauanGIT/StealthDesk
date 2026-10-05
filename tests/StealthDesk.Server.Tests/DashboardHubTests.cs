using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Dashboard;

namespace StealthDesk.Server.Tests;

public class DashboardHubTests
{
  [Fact]
  public async Task Dashboard_AcceptsBrowserConnections()
  {
    using var server = ServerHost.InMemory();
    await using var dashboard = await TestDashboard.ConnectSignedInAsync(server);

    Assert.Equal(HubConnectionState.Connected, dashboard.Connection.State);
  }

  [Fact]
  public async Task DeviceChanged_SentByTheServer_ReachesConnectedDashboards()
  {
    using var server = ServerHost.InMemory();
    await using var dashboard = await TestDashboard.ConnectSignedInAsync(server);

    var sent = new DeviceSummary
    {
      Id = Guid.NewGuid(),
      Name = "FRONT-DESK",
      Platform = DevicePlatform.Windows,
      IsOnline = true,
      Disks = [new DiskInfo { Name = @"C:\", SizeGb = 256 }],
    };
    var hub = server.Services.GetRequiredService<IHubContext<DashboardHub, IDashboardCallbacks>>();
    await hub.Clients.All.DeviceChanged(sent);

    var device = await dashboard.NextChangeAsync();
    Assert.Equal(sent.Id, device.Id);
    Assert.Equal("FRONT-DESK", device.Name);
    Assert.True(device.IsOnline);
    Assert.Equal(256, Assert.Single(device.Disks).SizeGb);
  }
}
