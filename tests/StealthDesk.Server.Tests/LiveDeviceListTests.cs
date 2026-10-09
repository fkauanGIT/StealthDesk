using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Client.Devices;

namespace StealthDesk.Server.Tests;

/// <summary>End to end: the web client's device store, kept current by its live updates, against the real server.</summary>
public class LiveDeviceListTests
{
  [Fact]
  public async Task ListedDevice_GoingOfflineAndOnline_ReachesTheStoreWithoutReloading()
  {
    using var server = ServerHost.InMemory();
    var deviceId = Guid.NewGuid();
    var agent = await TestAgent.ConnectAsync(server);
    await agent.ReportAsync(TestAgent.Report(deviceId));
    var (store, live) = await OpenDashboardAsync(server);
    await using var _ = live;

    await store.LoadAsync();
    await live.StartAsync();
    Assert.True(store.Devices!.Single().IsOnline);

    await agent.DisposeAsync();
    Assert.True(await Eventually.TrueAsync(() => Task.FromResult(!store.Devices!.Single().IsOnline)), "The device never turned offline.");

    await using var back = await TestAgent.ConnectAsync(server, agent.Keys);
    await back.ReportAsync(TestAgent.Report(deviceId));
    Assert.True(await Eventually.TrueAsync(() => Task.FromResult(store.Devices!.Single().IsOnline)), "The device never came back online.");
  }

  [Fact]
  public async Task NewDevice_ShowsUpOnTheNextLoad_AndThenUpdatesLive()
  {
    using var server = ServerHost.InMemory();
    var (store, live) = await OpenDashboardAsync(server);
    await using var _ = live;
    await live.StartAsync();
    await store.LoadAsync();

    var agent = await TestAgent.ConnectAsync(server);
    var deviceId = Guid.NewGuid();
    await agent.ReportAsync(TestAgent.Report(deviceId));
    await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    Assert.Empty(store.Devices!);

    await store.LoadAsync();
    Assert.Equal(deviceId, store.Devices!.Single().Id);
    await live.Subscribed;

    // Loading subscribed to it: its next change arrives on its own.
    await agent.DisposeAsync();
    Assert.True(await Eventually.TrueAsync(() => Task.FromResult(!store.Devices!.Single().IsOnline)), "The device never turned offline.");
  }

  private static async Task<(DeviceStore Store, LiveUpdates Live)> OpenDashboardAsync(ServerHost server)
  {
    var client = await TestAccounts.SignedInClientAsync(server);
    var cookie = client.DefaultRequestHeaders.GetValues("Cookie").Single();
    var store = new DeviceStore(client);
    var live = new LiveUpdates(
      store,
      new Uri(server.Server.BaseAddress, Routes.Dashboard),
      NullLogger<LiveUpdates>.Instance,
      options =>
      {
        options.HttpMessageHandlerFactory = _ => server.Server.CreateHandler();
        options.Transports = HttpTransportType.LongPolling;
        options.Headers["Cookie"] = cookie;
      });
    return (store, live);
  }
}
