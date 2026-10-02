using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts.Devices;
using StealthDesk.Web.Client.Devices;
using StealthDesk.Web.Client.Pages;

namespace StealthDesk.Web.Client.Tests;

public class DeviceDetailsTests : BunitContext
{
  private readonly FakeApi _api = new();

  public DeviceDetailsTests()
  {
    Services.AddSingleton(_api.CreateClient());
    Services.AddSingleton<DeviceStore>();
    Services.AddSingleton<ILiveUpdates>(new FakeLiveUpdates());
  }

  private DeviceStore Store => Services.GetRequiredService<DeviceStore>();

  [Fact]
  public void OpenedDirectly_ShowsTheDeviceFromTheServer()
  {
    var device = Detailed();
    _api.RespondWith(device);

    var page = Render<DeviceDetails>(x => x.Add(p => p.Id, device.Id));

    page.WaitForAssertion(() => Assert.Equal("FRONT-DESK", page.Find("h1").TextContent));
    var text = page.Markup;
    Assert.Contains("front-desk.corp.local", text);
    Assert.Contains("0.2.0", text);
    Assert.Contains("10.0.0.20", text);
    Assert.Contains("203.0.113.7", text);
    Assert.Contains("00155D012345, 00155D067890", text);
    var disk = page.FindAll(".disks tbody td").Select(x => x.TextContent.Trim()).ToList();
    Assert.Equal([@"C:\", "System", "NTFS", "210 / 512 GB", "302 GB"], disk);
  }

  [Fact]
  public async Task OpenedFromTheList_UsesTheDeviceAlreadyLoaded()
  {
    var device = Detailed();
    _api.RespondWith(new[] { device });
    await Store.LoadAsync();

    var page = Render<DeviceDetails>(x => x.Add(p => p.Id, device.Id));

    // No second answer was queued: a request would leave the page loading.
    page.WaitForAssertion(() => Assert.Equal("FRONT-DESK", page.Find("h1").TextContent));
  }

  [Fact]
  public void UnknownDevice_SaysNotFound()
  {
    _api.Respond(HttpStatusCode.NotFound);

    var page = Render<DeviceDetails>(x => x.Add(p => p.Id, Guid.NewGuid()));

    page.WaitForAssertion(() => Assert.Contains("Device not found", page.Markup));
  }

  [Fact]
  public void ServerError_ShowsAnError()
  {
    _api.Respond(HttpStatusCode.InternalServerError);

    var page = Render<DeviceDetails>(x => x.Add(p => p.Id, Guid.NewGuid()));

    page.WaitForAssertion(() => Assert.Single(page.FindAll(".alert-danger")));
    Assert.DoesNotContain("Device not found", page.Markup);
  }

  [Fact]
  public void PushedChange_UpdatesTheDetailsLive()
  {
    var device = Detailed();
    _api.RespondWith(device);
    var page = Render<DeviceDetails>(x => x.Add(p => p.Id, device.Id));
    page.WaitForAssertion(() => Assert.Equal("Online", page.Find(".device-status").TextContent));

    Store.Apply(device with { IsOnline = false, LastSeen = device.LastSeen.AddMinutes(1) });

    page.WaitForAssertion(() => Assert.Equal("Offline", page.Find(".device-status").TextContent));
  }

  [Fact]
  public async Task PushedChangeForAnotherDevice_LeavesThePageAlone()
  {
    var device = Detailed();
    var other = SampleDevices.Sample("OTHER-PC");
    _api.RespondWith(new[] { device, other });
    await Store.LoadAsync();
    var page = Render<DeviceDetails>(x => x.Add(p => p.Id, device.Id));
    page.WaitForAssertion(() => Assert.Equal("FRONT-DESK", page.Find("h1").TextContent));

    Store.Apply(other with { IsOnline = false, LastSeen = other.LastSeen.AddMinutes(1) });

    Assert.Equal("FRONT-DESK", page.Find("h1").TextContent);
    Assert.Equal("Online", page.Find(".device-status").TextContent);
  }

  private static DeviceSummary Detailed() => SampleDevices.Sample("FRONT-DESK") with
  {
    DnsName = "front-desk.corp.local",
    AgentVersion = "0.2.0",
    LocalIpV4 = "10.0.0.20",
    PublicIpV4 = "203.0.113.7",
    MacAddresses = ["00155D012345", "00155D067890"],
    Disks = [new DiskInfo { Name = @"C:\", Label = "System", Format = "NTFS", SizeGb = 512, FreeGb = 302 }],
  };
}
