using StealthDesk.Contracts.Devices;
using StealthDesk.Web.Client.Devices;

namespace StealthDesk.Web.Client.Tests;

public class DeviceStoreTests
{
  private readonly FakeApi _api = new();
  private readonly DeviceStore _store;

  public DeviceStoreTests()
  {
    _store = new DeviceStore(_api.CreateClient());
  }

  [Fact]
  public async Task Devices_AreOrderedByName()
  {
    _api.RespondWith(new[] { SampleDevices.Sample("zeta"), SampleDevices.Sample("Alpha") });

    await _store.LoadAsync();

    Assert.Equal(["Alpha", "zeta"], _store.Devices!.Select(x => x.Name));
  }

  [Fact]
  public async Task ChangeOlderThanWhatIsKnown_IsIgnored()
  {
    var device = SampleDevices.Sample("FRONT-DESK", isOnline: false);
    _api.RespondWith(new[] { device });
    await _store.LoadAsync();

    // A delayed message from before the device went offline.
    _store.Apply(device with { IsOnline = true, LastSeen = device.LastSeen.AddMinutes(-1) });

    Assert.False(Assert.Single(_store.Devices!).IsOnline);
  }

  [Fact]
  public async Task ChangePushedWhileLoading_IsKept()
  {
    var device = SampleDevices.Sample("FRONT-DESK", isOnline: true);
    var loading = _store.LoadAsync();

    // The answer was read before the device went offline, but arrives after the push.
    _store.Apply(device with { IsOnline = false, LastSeen = device.LastSeen.AddMinutes(1) });
    _store.Apply(SampleDevices.Sample("NEW-PC"));
    _api.RespondWith(new[] { device });
    await loading;

    Assert.Equal(["FRONT-DESK", "NEW-PC"], _store.Devices!.Select(x => x.Name));
    Assert.False(_store.Devices![0].IsOnline);
  }

  [Fact]
  public async Task Reload_DropsDevicesTheServerNoLongerHas()
  {
    _api.RespondWith(new[] { SampleDevices.Sample("KEPT-PC"), SampleDevices.Sample("GONE-PC") });
    await _store.LoadAsync();

    // E.g. the server restarted on a fresh database.
    _api.RespondWith(new[] { _store.Devices!.Single(x => x.Name == "KEPT-PC") });
    await _store.LoadAsync();

    Assert.Equal(["KEPT-PC"], _store.Devices!.Select(x => x.Name));
  }

  [Fact]
  public async Task Reload_BringsInWhatWasMissedWhileDisconnected()
  {
    var device = SampleDevices.Sample("FRONT-DESK", isOnline: true);
    _api.RespondWith(new[] { device });
    await _store.LoadAsync();

    // The server restarted and marked it offline without changing when it was last seen.
    _api.RespondWith(new[] { device with { IsOnline = false } });
    await _store.LoadAsync();

    Assert.False(Assert.Single(_store.Devices!).IsOnline);
  }

  [Fact]
  public async Task FailedReload_KeepsTheListAndReportsTheError()
  {
    _api.RespondWith(new[] { SampleDevices.Sample("FRONT-DESK") });
    await _store.LoadAsync();

    _api.Respond(System.Net.HttpStatusCode.InternalServerError);
    await _store.LoadAsync();

    Assert.Single(_store.Devices!);
    Assert.NotNull(_store.Error);
  }

  [Fact]
  public void Changes_AreAnnounced()
  {
    var announced = 0;
    _store.Changed += () => announced++;

    _store.Apply(SampleDevices.Sample("FRONT-DESK"));

    Assert.Equal(1, announced);
  }
}
