using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Dashboard;

namespace StealthDesk.Server.Tests;

/// <summary>The dashboard hub: connections, and device subscriptions limited to what the user may read.</summary>
public class DashboardHubTests
{
  private static readonly TimeSpan Silence = TimeSpan.FromSeconds(1);
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

  [Fact]
  public async Task Subscribe_AcceptsOnlyTheDevicesTheUserMayRead()
  {
    using var server = ServerHost.InMemory();
    await using var first = await ReportingAgentAsync(server);
    await using var second = await ReportingAgentAsync(server);
    var user = await TestAccounts.CreateUserAsync(server, "tech@example.com");
    await TestPermissions.AssignAsync(server, user, PermissionNames.DeviceRead, PermissionScopeKind.Device, first.DeviceId);
    await using var dashboard = await TestDashboard.ConnectAsync(server, await TestAccounts.SignInForCookieAsync(server, "tech@example.com"));

    var accepted = await dashboard.SubscribeAsync(first.DeviceId, second.DeviceId, Guid.NewGuid());
    await second.ReportAgainAsync();
    await first.ReportAgainAsync();

    Assert.Equal([first.DeviceId], accepted);
    Assert.Equal(first.DeviceId, (await dashboard.NextChangeAsync()).Id);
    await Assert.ThrowsAsync<TimeoutException>(() => dashboard.NextChangeAsync(within: Silence));
  }

  [Fact]
  public async Task NewDevice_ReachesNoDashboard_UntilItIsLoadedAndSubscribed()
  {
    using var server = ServerHost.InMemory();
    await using var dashboard = await TestDashboard.ConnectSignedInAsync(server);

    await using var agent = await ReportingAgentAsync(server);

    await Assert.ThrowsAsync<TimeoutException>(() => dashboard.NextChangeAsync(within: Silence));
    Assert.Equal([agent.DeviceId], await dashboard.SubscribeAsync(agent.DeviceId));
  }

  [Fact]
  public async Task Unsubscribe_StopsTheUpdates()
  {
    using var server = ServerHost.InMemory();
    await using var agent = await ReportingAgentAsync(server);
    await using var dashboard = await TestDashboard.ConnectSignedInAsync(server);
    await dashboard.SubscribeAsync(agent.DeviceId);

    await dashboard.Connection.InvokeAsync(nameof(IDashboardHub.UnsubscribeFromDevices), new[] { agent.DeviceId }, TestContext.Current.CancellationToken);
    await agent.ReportAgainAsync();

    await Assert.ThrowsAsync<TimeoutException>(() => dashboard.NextChangeAsync(within: Silence));
  }

  [Fact]
  public async Task Subscribe_RefusesMoreDevicesThanOneCallMayCarry()
  {
    using var server = ServerHost.InMemory();
    await using var dashboard = await TestDashboard.ConnectSignedInAsync(server);
    var ids = Enumerable.Range(0, IDashboardHub.MaxDevicesPerSubscription + 1).Select(_ => Guid.NewGuid()).ToArray();

    var reply = await dashboard.Connection.InvokeAsync<GatewayReply<IReadOnlyList<Guid>>>(
      nameof(IDashboardHub.SubscribeToDevices), ids, TestContext.Current.CancellationToken);

    Assert.False(reply.Accepted);
    Assert.Contains("at most 100", reply.Error);
  }

  private static async Task<ReportingAgent> ReportingAgentAsync(ServerHost server)
  {
    var agent = new ReportingAgent(await TestAgent.ConnectAsync(server), Guid.NewGuid());
    await agent.ReportAgainAsync();
    return agent;
  }

  private sealed record ReportingAgent(TestAgent Agent, Guid DeviceId) : IAsyncDisposable
  {
    public async Task ReportAgainAsync()
    {
      var reply = await Agent.ReportAsync(TestAgent.Report(DeviceId));
      Assert.True(reply.Accepted, reply.Error);
    }

    public ValueTask DisposeAsync() => Agent.DisposeAsync();
  }
}
