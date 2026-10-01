using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts.Devices;
using StealthDesk.Web.Client.Pages;

namespace StealthDesk.Web.Client.Tests;

public class HomeTests : BunitContext
{
  private readonly FakeApi _api = new();

  public HomeTests()
  {
    Services.AddSingleton(_api.CreateClient());
  }

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
    _api.RespondWith(new[] { Device("FRONT-DESK", isOnline: true) });

    var page = Render<Home>();

    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));
    var cells = page.FindAll("tbody td").Select(x => x.TextContent.Trim()).ToList();
    Assert.Contains("FRONT-DESK", cells);
    Assert.Contains("Microsoft Windows 11 Pro", cells);
    Assert.Contains("alice, bob", cells);
    Assert.Contains(cells, x => x.StartsWith("8 / 16 GB", StringComparison.Ordinal));
  }

  [Fact]
  public void Status_IsShownWithTextNotOnlyColor()
  {
    _api.RespondWith(new[] { Device("ONLINE-PC", isOnline: true), Device("OFFLINE-PC", isOnline: false) });

    var page = Render<Home>();

    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".badge").Count));
    var badges = page.FindAll(".badge");
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

  private static DeviceSummary Device(string name, bool isOnline) => new()
  {
    Id = Guid.NewGuid(),
    Name = name,
    OsDescription = "Microsoft Windows 11 Pro",
    CpuLoad = 0.25,
    MemoryTotalGb = 16,
    MemoryUsedGb = 8,
    StorageTotalGb = 512,
    StorageUsedGb = 256,
    LoggedOnUsers = ["alice", "bob"],
    IsOnline = isOnline,
    LastSeen = DateTimeOffset.UtcNow,
  };
}
