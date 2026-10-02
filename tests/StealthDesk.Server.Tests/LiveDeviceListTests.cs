using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Client.Devices;

namespace StealthDesk.Server.Tests;

/// <summary>End to end: the web client's device store, kept current by its live updates, against the real server.</summary>
public class LiveDeviceListTests
{
  [Fact]
  public async Task AgentGoingOnlineAndOffline_ReachesTheStoreWithoutReloading()
  {
    using var server = ServerHost.InMemory();
    var store = new DeviceStore(server.CreateClient());
    await using var live = new LiveUpdates(
      store,
      new Uri(server.Server.BaseAddress, Routes.Dashboard),
      NullLogger<LiveUpdates>.Instance,
      options =>
      {
        options.HttpMessageHandlerFactory = _ => server.Server.CreateHandler();
        options.Transports = HttpTransportType.LongPolling;
      });

    await live.StartAsync();
    await store.LoadAsync();
    Assert.Equal(LiveState.Live, live.State);
    Assert.Empty(store.Devices!);

    var agent = await TestAgent.ConnectAsync(server);
    var deviceId = Guid.NewGuid();
    await agent.ReportAsync(TestAgent.Report(deviceId));

    Assert.True(
      await Eventually.TrueAsync(() => Task.FromResult(store.Devices!.Any(x => x.Id == deviceId && x.IsOnline))),
      "The device never showed up online.");

    await agent.DisposeAsync();

    Assert.True(
      await Eventually.TrueAsync(() => Task.FromResult(store.Devices!.Any(x => x.Id == deviceId && !x.IsOnline))),
      "The device never turned offline.");
  }
}
