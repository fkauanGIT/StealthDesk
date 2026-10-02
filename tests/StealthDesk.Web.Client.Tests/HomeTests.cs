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
    Assert.Contains("FRONT-DESK", cells);
    Assert.Contains("Microsoft Windows 11 Pro", cells);
    Assert.Contains("alice, bob", cells);
    Assert.Contains(cells, x => x.StartsWith("8 / 16 GB", StringComparison.Ordinal));
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

    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody .badge").Count));
    var badges = page.FindAll("tbody .badge");
    Assert.Equal("Online", badges[0].TextContent);
    Assert.Contains("text-bg-success", badges[0].ClassList);
    Assert.Equal("Offline", badges[1].TextContent);
    Assert.Contains("text-bg-secondary", badges[1].ClassList);
  }

  [Fact]
  public void ServerError_ShowsAnErrorInsteadOfLoadingForever()
  {
    _api.Respond(HttpStatusCode.NotFound);

    var page = Render<Home>();

    page.WaitForAssertion(() => Assert.Single(page.FindAll(".alert-danger")));
    Assert.DoesNotContain("Loading devices", page.Markup);
  }

  [Fact]
  public void ResponseThatIsNotJson_ShowsAnError()
  {
    _api.RespondWithHtml();

    var page = Render<Home>();

    page.WaitForAssertion(() => Assert.Single(page.FindAll(".alert-danger")));
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
    page.WaitForAssertion(() => Assert.Equal("Online", page.Find("tbody .badge").TextContent));

    Store.Apply(device with { IsOnline = false, LastSeen = device.LastSeen.AddMinutes(1) });

    page.WaitForAssertion(() => Assert.Equal("Offline", page.Find("tbody .badge").TextContent));
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
  [InlineData(LiveState.Connecting, "Connecting...", "text-bg-secondary")]
  [InlineData(LiveState.Live, "Live", "text-bg-success")]
  [InlineData(LiveState.Reconnecting, "Reconnecting...", "text-bg-warning")]
  public void Indicator_ShowsTheConnectionWithText(LiveState state, string text, string color)
  {
    var page = Render<Home>();

    _live.Become(state);

    page.WaitForAssertion(() => Assert.Equal(text, page.Find(".live-indicator").TextContent));
    Assert.Contains(color, page.Find(".live-indicator").ClassList);
  }
}
