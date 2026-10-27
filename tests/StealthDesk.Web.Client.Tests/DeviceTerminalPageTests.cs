using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Terminal;
using StealthDesk.Web.Client.Devices;
using StealthDesk.Web.Client.Pages;
using StealthDesk.Web.Client.Terminal;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The terminal action on a device's page, and the terminal page itself.</summary>
public class DeviceTerminalPageTests : BunitContext
{
  private readonly FakeApi _api = new();
  private readonly FakeDeviceHub _hub = new();
  private readonly FakeLiveUpdates _live = new();
  private readonly DeviceSummary _device = SampleDevices.Sample("FRONT-DESK");

  public DeviceTerminalPageTests()
  {
    Services.AddSingleton(_api.CreateClient());
    Services.AddSingleton<DeviceStore>();
    Services.AddSingleton<ILiveUpdates>(_live);
    Services.AddSingleton<IDeviceHub>(_hub);
    Services.AddSingleton<ITerminalKeys, NoKeys>();
    Services.AddSingleton<DeviceTerminal>();
    Services.AddSingleton<StealthDesk.Web.Client.DeviceGroups.DeviceGroupApi>();
    AddAuthorization();
    _live.Become(LiveState.Live);
  }

  private DeviceTerminal Terminal => Services.GetRequiredService<DeviceTerminal>();

  [Fact]
  public async Task TheDevicesPage_OffersTheTerminal_WithThePermission()
  {
    await LoadAsync(_device);

    var page = Render<DeviceDetails>(x => x.Add(p => p.Id, _device.Id));

    page.WaitForAssertion(() => Assert.Equal($"devices/{_device.Id}/terminal", page.Find(".sd-device-actions a").GetAttribute("href")));
  }

  [Fact]
  public async Task TheDevicesPage_HidesTheTerminal_WithoutThePermission()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false));
    await LoadAsync(_device);

    var page = Render<DeviceDetails>(x => x.Add(p => p.Id, _device.Id));

    page.WaitForAssertion(() => Assert.Contains($"actions {_device.Id}", _hub.Calls));
    page.WaitForAssertion(() => Assert.Equal("FRONT-DESK", page.Find("h1").TextContent));
    Assert.Empty(page.FindAll(".sd-device-actions"));
    Assert.DoesNotContain("Terminal", page.Find("header").TextContent);
  }

  [Fact]
  public async Task TheDevicesPage_DisablesTheTerminal_WhileOffline_SayingWhy()
  {
    var offline = _device with { IsOnline = false };
    await LoadAsync(offline);

    var page = Render<DeviceDetails>(x => x.Add(p => p.Id, offline.Id));

    page.WaitForAssertion(() => Assert.True(page.Find(".sd-device-actions button").HasAttribute("disabled")));
    Assert.Equal("Available when the device is online", page.Find("#terminal-offline").TextContent);
    Assert.Empty(page.FindAll(".sd-device-actions a"));
  }

  [Fact]
  public async Task ThePage_OpensASession_AndShowsItsOutput_ErrorsInTheErrorColor()
  {
    await LoadAsync(_device);

    var page = Render<DeviceTerminalPage>(x => x.Add(p => p.Id, _device.Id));
    page.WaitForAssertion(() => Assert.Contains("PS C:\\>", page.Find(".sd-terminal-screen").TextContent));
    _hub.Send(_hub.NextTerminal, TerminalOutputKind.Error, "boom\n");

    page.WaitForAssertion(() => Assert.Equal("boom", page.Find(".sd-terminal-error").TextContent));
    Assert.Contains("PowerShell 7.6.8 on Windows", page.Find(".sd-terminal-screen").TextContent);
    Assert.False(page.Find("textarea").HasAttribute("disabled"));
  }

  [Fact]
  public async Task Keys_RunTheCommand_WalkTheHistory_AndClear()
  {
    await LoadAsync(_device);
    var page = Render<DeviceTerminalPage>(x => x.Add(p => p.Id, _device.Id));
    page.WaitForAssertion(() => Assert.Equal(TerminalStatus.Open, Terminal.Status));
    var input = () => page.Find("textarea");

    input().Input("hostname");
    await input().KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
    page.WaitForAssertion(() => Assert.Contains($"input {_hub.NextTerminal} hostname", _hub.Calls));
    input().Input("Get-");
    await input().KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" });
    Assert.Equal("hostname", input().GetAttribute("value"));
    await input().KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
    Assert.Equal("Get-", input().GetAttribute("value"));
    await input().KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
    Assert.Equal(string.Empty, input().GetAttribute("value"));
  }

  [Fact]
  public async Task Multiline_ShiftEnterAddsALine_AndEnterStillRuns()
  {
    await LoadAsync(_device);
    var page = Render<DeviceTerminalPage>(x => x.Add(p => p.Id, _device.Id));
    page.WaitForAssertion(() => Assert.Equal(TerminalStatus.Open, Terminal.Status));

    page.Find(".sd-checkbox input").Change(true);
    var input = page.Find("textarea");
    Assert.Equal("true", input.GetAttribute("data-multiline"));
    input.Input("$a = 1\n$a + 1");
    await page.Find("textarea").KeyDownAsync(new KeyboardEventArgs { Key = "Enter", ShiftKey = true });
    Assert.DoesNotContain(_hub.Calls, x => x.StartsWith("input", StringComparison.Ordinal));
    await page.Find("textarea").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

    page.WaitForAssertion(() => Assert.Contains($"input {_hub.NextTerminal} $a = 1\n$a + 1", _hub.Calls));
  }

  [Fact]
  public async Task WithoutThePermission_ThePageSaysSo_AndOpensNothing()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false));
    await LoadAsync(_device);

    var page = Render<DeviceTerminalPage>(x => x.Add(p => p.Id, _device.Id));

    page.WaitForAssertion(() => Assert.Contains("You may not use the terminal on this device.", page.Markup));
    Assert.DoesNotContain(_hub.Calls, x => x.StartsWith("open", StringComparison.Ordinal));
    Assert.Empty(page.FindAll("textarea"));
  }

  [Fact]
  public async Task AnOfflineDevice_OrARefusal_ShowsAMessage()
  {
    await LoadAsync(_device with { IsOnline = false });

    var page = Render<DeviceTerminalPage>(x => x.Add(p => p.Id, _device.Id));

    page.WaitForAssertion(() => Assert.Contains("The device is offline. A session can open once it is back.", page.Markup));
    Assert.DoesNotContain(_hub.Calls, x => x.StartsWith("open", StringComparison.Ordinal));
    Assert.True(page.Find("textarea").HasAttribute("disabled"));

    _hub.OpenReply = _ => GatewayReply.Refuse<Guid>("The device didn't answer within 30 seconds.");
    Services.GetRequiredService<DeviceStore>().Apply(_device with { LastSeen = _device.LastSeen.AddMinutes(1) });
    page.WaitForAssertion(() => Assert.False(page.FindAll(".sd-terminal-actions button").Last().HasAttribute("disabled")));
    await page.FindAll(".sd-terminal-actions button").Last().ClickAsync(new MouseEventArgs());

    page.WaitForAssertion(() => Assert.Contains("The device didn't answer within 30 seconds.", page.Find(".sd-alert--danger").TextContent));
  }

  [Fact]
  public async Task TheShortcutsDialog_ListsEveryKey()
  {
    await LoadAsync(_device);
    var page = Render<DeviceTerminalPage>(x => x.Add(p => p.Id, _device.Id));
    page.WaitForAssertion(() => Assert.Equal(TerminalStatus.Open, Terminal.Status));

    await page.FindAll("button").Single(x => x.TextContent == "Keyboard shortcuts").ClickAsync(new MouseEventArgs());

    var keys = page.FindAll(".sd-shortcuts kbd").Select(x => x.TextContent).ToList();
    Assert.Equal(DeviceTerminalPage.Shortcuts.Select(x => x.Keys), keys);
    Assert.Equal(["Enter", "Shift + Enter", "Up", "Down", "Escape"], keys);
  }

  private Task LoadAsync(DeviceSummary device)
  {
    _api.RespondWith(new[] { device });
    return Services.GetRequiredService<DeviceStore>().LoadAsync();
  }

  private sealed class NoKeys : ITerminalKeys
  {
    public Task AttachAsync(ElementReference input) => Task.CompletedTask;
  }
}
