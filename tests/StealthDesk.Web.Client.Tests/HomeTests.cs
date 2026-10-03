using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts.Devices;
using StealthDesk.Web.Client.Devices;
using StealthDesk.Web.Client.Pages;

namespace StealthDesk.Web.Client.Tests;

public class HomeTests : BunitContext
{
  private readonly FakeApi _api = new();
  private readonly FakeLiveUpdates _live = new();

  public HomeTests()
  {
    Services.AddSingleton(_api.CreateClient());
    Services.AddSingleton<DeviceStore>();
    Services.AddSingleton<ILiveUpdates>(_live);
  }

  private DeviceStore Store => Services.GetRequiredService<DeviceStore>();

  [Fact]
  public void WhileWaitingForTheServer_ShowsLoading()
  {
    var page = Render<Home>();

    Assert.Contains("Loading devices", page.Markup);
    Assert.Empty(page.FindAll("table"));
  }

  [Fact]
  public void NoDevices_SaysSoInsteadOfAnEmptyTable()
  {
    _api.RespondWith(Array.Empty<DeviceSummary>());

    var page = Render<Home>();

    page.WaitForAssertion(() => Assert.Contains("No devices yet", page.Markup));
    Assert.Empty(page.FindAll("table"));
  }

  [Fact]
  public void Devices_AreListedWithTheirUsage()
  {
    _api.RespondWith(new[] { SampleDevices.Sample("FRONT-DESK") });

    var page = Render<Home>();

    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));
    var cells = page.FindAll("tbody td").Select(x => x.TextContent.Trim()).ToList();
    Assert.Equal("FRONT-DESK", page.Find("tbody a").TextContent);
    Assert.Equal("Microsoft Windows 11 Pro", page.Find("tbody .sd-cell-sub").TextContent);
    Assert.Contains("alice, bob", cells);
    Assert.Contains(cells, x => x.StartsWith("8 / 16 GB", StringComparison.Ordinal));
  }

  [Fact]
  public void Summary_CountsOnlineAndOfflineDevices()
  {
    _api.RespondWith(new[] { SampleDevices.Sample("A", isOnline: true), SampleDevices.Sample("B", isOnline: true), SampleDevices.Sample("C", isOnline: false) });

    var page = Render<Home>();

    page.WaitForAssertion(() => Assert.Equal("3", page.Find("[data-stat=total]").TextContent));
    Assert.Equal("2", page.Find("[data-stat=online]").TextContent);
    Assert.Equal("1", page.Find("[data-stat=offline]").TextContent);
  }

  [Fact]
  public void StatusFilter_ShowsOnlyTheChosenDevices()
  {
    _api.RespondWith(new[] { SampleDevices.Sample("A-ONLINE", isOnline: true), SampleDevices.Sample("B-OFFLINE", isOnline: false) });
    var page = Render<Home>();
    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody tr").Count));

    page.FindAll(".sd-segmented button").Single(x => x.TextContent == "Offline").Click();

    Assert.Equal("B-OFFLINE", page.Find("tbody a").TextContent);
    Assert.Single(page.FindAll("tbody tr"));
    Assert.Equal("true", page.FindAll(".sd-segmented button").Single(x => x.TextContent == "Offline").GetAttribute("aria-pressed"));
  }

  [Theory]
  [InlineData("front")]
  [InlineData("bob")]
  [InlineData("10.0.0.20")]
  public void Search_MatchesNameUserOrAddress(string term)
  {
    var front = SampleDevices.Sample("FRONT-DESK") with { LocalIpV4 = "10.0.0.20", LoggedOnUsers = ["bob"] };
    var other = SampleDevices.Sample("BACK-OFFICE") with { LocalIpV4 = "10.0.0.30", LoggedOnUsers = ["carol"] };
    _api.RespondWith(new[] { front, other });
    var page = Render<Home>();
    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody tr").Count));

    page.Find("#device-search").Input(term);

    Assert.Equal("FRONT-DESK", Assert.Single(page.FindAll("tbody a")).TextContent);
  }

  [Fact]
  public void SearchWithoutMatches_SaysSo()
  {
    _api.RespondWith(new[] { SampleDevices.Sample("FRONT-DESK") });
    var page = Render<Home>();
    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));

    page.Find("#device-search").Input("nothing-like-this");

    Assert.Contains("No devices match", page.Markup);
    Assert.Empty(page.FindAll("table"));
  }

  [Fact]
  public void OfflineDevice_DoesNotShowStaleCpuAndMemory()
  {
    _api.RespondWith(new[] { SampleDevices.Sample("WAREHOUSE", isOnline: false) });

    var page = Render<Home>();

    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));
    var meters = page.FindAll("tbody [role=meter]").Select(x => x.GetAttribute("aria-label")).ToList();
    Assert.Equal(["Storage"], meters);
  }

  [Fact]
  public void DeviceName_LinksToItsDetails()
  {
    var device = SampleDevices.Sample("FRONT-DESK");
    _api.RespondWith(new[] { device });

    var page = Render<Home>();

    page.WaitForAssertion(() => Assert.Equal($"devices/{device.Id}", page.Find("tbody a").GetAttribute("href")));
  }

  [Fact]
  public void Status_IsShownWithTextNotOnlyColor()
  {
    _api.RespondWith(new[] { SampleDevices.Sample("A-ONLINE", isOnline: true), SampleDevices.Sample("B-OFFLINE", isOnline: false) });

    var page = Render<Home>();

    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody .device-status").Count));
    var badges = page.FindAll("tbody .device-status");
    Assert.Equal("Online", badges[0].TextContent);
    Assert.Contains("sd-status--online", badges[0].ClassList);
    Assert.Equal("Offline", badges[1].TextContent);
    Assert.Contains("sd-status--offline", badges[1].ClassList);
  }

  [Fact]
  public void ServerError_ShowsAnErrorInsteadOfLoadingForever()
  {
    _api.Respond(HttpStatusCode.NotFound);

    var page = Render<Home>();

    page.WaitForAssertion(() => Assert.Single(page.FindAll(".sd-alert--danger")));
    Assert.DoesNotContain("Loading devices", page.Markup);
  }

  [Fact]
  public void ResponseThatIsNotJson_ShowsAnError()
  {
    _api.RespondWithHtml();

    var page = Render<Home>();

    page.WaitForAssertion(() => Assert.Single(page.FindAll(".sd-alert--danger")));
  }

  [Fact]
  public void Opening_StartsLiveUpdates()
  {
    Render<Home>();

    Assert.True(_live.Started);
  }

  [Fact]
  public void PushedChange_UpdatesTheRowWithoutReloading()
  {
    var device = SampleDevices.Sample("FRONT-DESK", isOnline: true);
    _api.RespondWith(new[] { device });
    var page = Render<Home>();
    page.WaitForAssertion(() => Assert.Equal("Online", page.Find("tbody .device-status").TextContent));

    Store.Apply(device with { IsOnline = false, LastSeen = device.LastSeen.AddMinutes(1) });

    page.WaitForAssertion(() => Assert.Equal("Offline", page.Find("tbody .device-status").TextContent));
    Assert.Single(page.FindAll("tbody tr"));
  }

  [Fact]
  public void PushedNewDevice_IsAddedToTheList()
  {
    _api.RespondWith(Array.Empty<DeviceSummary>());
    var page = Render<Home>();
    page.WaitForAssertion(() => Assert.Contains("No devices yet", page.Markup));

    Store.Apply(SampleDevices.Sample("NEW-PC"));

    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));
    Assert.Contains("NEW-PC", page.Find("tbody").TextContent);
  }

  [Theory]
  [InlineData(LiveState.Connecting, "Connecting...", "sd-status--neutral")]
  [InlineData(LiveState.Live, "Live", "sd-status--live")]
  [InlineData(LiveState.Reconnecting, "Reconnecting...", "sd-status--warning")]
  public void Indicator_ShowsTheConnectionWithText(LiveState state, string text, string color)
  {
    var page = Render<Home>();

    _live.Become(state);

    page.WaitForAssertion(() => Assert.Equal(text, page.Find(".live-indicator").TextContent));
    Assert.Contains(color, page.Find(".live-indicator").ClassList);
  }
}
