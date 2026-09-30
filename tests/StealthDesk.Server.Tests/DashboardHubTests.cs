using Microsoft.AspNetCore.Http.Connections;
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
    await using var dashboard = CreateDashboard(server);

    await dashboard.StartAsync(TestContext.Current.CancellationToken);

    Assert.Equal(HubConnectionState.Connected, dashboard.State);
  }

  [Fact]
  public async Task DeviceChanged_SentByTheServer_ReachesConnectedDashboards()
  {
    using var server = ServerHost.InMemory();
    await using var dashboard = CreateDashboard(server);
    var received = new TaskCompletionSource<DeviceSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
    dashboard.On<DeviceSummary>(nameof(IDashboardCallbacks.DeviceChanged), received.SetResult);
    await dashboard.StartAsync(TestContext.Current.CancellationToken);

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

    var device = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    Assert.Equal(sent.Id, device.Id);
    Assert.Equal("FRONT-DESK", device.Name);
    Assert.True(device.IsOnline);
    Assert.Equal(256, Assert.Single(device.Disks).SizeGb);
  }

  // JSON, as a browser would use. Long polling because the in-memory test server can't upgrade to WebSockets.
  private static HubConnection CreateDashboard(ServerHost server) =>
    new HubConnectionBuilder()
      .WithUrl(new Uri(server.Server.BaseAddress, Routes.Dashboard), options =>
      {
        options.HttpMessageHandlerFactory = _ => server.Server.CreateHandler();
        options.Transports = HttpTransportType.LongPolling;
      })
      .Build();
}
