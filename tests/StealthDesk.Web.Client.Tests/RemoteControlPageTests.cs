using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.Web.Client.Devices;
using StealthDesk.Web.Client.Pages;
using StealthDesk.Web.Client.RemoteControl;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The remote control action on a device's page, and the page's states, permissions and toolbar.</summary>
public class RemoteControlPageTests : BunitContext
{
  private readonly FakeApi _api = new();
  private readonly FakeDeviceHub _hub = new();
  private readonly FakeLiveUpdates _live = new();
  private readonly FakeRemoteDesktop _remote = new();
  private readonly DeviceSummary _device = SampleDevices.Sample("FRONT-DESK");

  public RemoteControlPageTests()
  {
    JSInterop.Mode = JSRuntimeMode.Loose;
    Services.AddSingleton(_api.CreateClient());
    Services.AddSingleton<DeviceStore>();
    Services.AddSingleton<ILiveUpdates>(_live);
    Services.AddSingleton<IDeviceHub>(_hub);
    Services.AddSingleton<IRemoteDesktopConnections>(_remote);
    Services.AddSingleton(TimeProvider.System);
    Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
    Services.AddSingleton<StealthDesk.Web.Client.DeviceGroups.DeviceGroupApi>();
    AddAuthorization();
    _live.Become(LiveState.Live);
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false, SeeDesktop: true, ControlDesktop: true, SendCtrlAltDel: true));
  }

  [Fact]
  public async Task TheDevicesPage_OffersRemoteControl_WithThePermission()
  {
    await LoadAsync(_device);

    var page = Render<DeviceDetails>(x => x.Add(p => p.Id, _device.Id));

    page.WaitForAssertion(() => Assert.Contains(
      page.FindAll(".sd-device-actions a"),
      a => a.GetAttribute("href") == $"devices/{_device.Id}/remote-control"));
  }

  [Fact]
  public async Task TheDevicesPage_HidesRemoteControl_WithoutThePermission()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false));
    await LoadAsync(_device);

    var page = Render<DeviceDetails>(x => x.Add(p => p.Id, _device.Id));

    page.WaitForAssertion(() => Assert.Contains($"actions {_device.Id}", _hub.Calls));
    Assert.DoesNotContain("Remote control", page.Find("header").TextContent);
  }

  [Fact]
  public async Task WithoutThePermission_ThePageSaysNotAllowed()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false, SeeDesktop: false));
    await LoadAsync(_device);

    var page = Render<RemoteControlPage>(x => x.Add(p => p.Id, _device.Id));

    page.WaitForAssertion(() => Assert.Contains("Not allowed", page.Markup));
  }

  [Fact]
  public async Task Offline_ThePageSaysSo_AndNeverConnects()
  {
    await LoadAsync(_device with { IsOnline = false });

    var page = Render<RemoteControlPage>(x => x.Add(p => p.Id, _device.Id));

    page.WaitForAssertion(() => Assert.Contains("Device offline", page.Markup));
    Assert.False(_remote.Started);
  }

  [Fact]
  public async Task WithSeveralSessions_ItOffersAChoice_ThenConnectsToTheChosenOne()
  {
    _hub.DesktopSessions = GatewayReply.Accept<IReadOnlyList<RemoteDesktopSession>>(
      [new RemoteDesktopSession(1, "console-user", IsConsole: true), new RemoteDesktopSession(3, "rdp-user", IsConsole: false)]);
    await LoadAsync(_device);

    var page = Render<RemoteControlPage>(x => x.Add(p => p.Id, _device.Id));
    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".sd-session-card").Count));
    Assert.False(_remote.Started);

    page.FindAll(".sd-session-card")[1].Click();

    page.WaitForAssertion(() => Assert.True(_remote.Started));
    Assert.Contains($"remote {_device.Id} 3", _hub.Calls);
  }

  [Fact]
  public async Task Controlling_CapturesInput_AndDrawsWhatArrives()
  {
    await LoadAsync(_device);

    var page = Render<RemoteControlPage>(x => x.Add(p => p.Id, _device.Id));
    page.WaitForAssertion(() => Assert.True(_remote.Started));

    // The viewer may control, so input capture is attached.
    page.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "attachInput"));
    Assert.Contains("Ctrl+Alt+Del", page.Markup);
    Assert.DoesNotContain("View only", page.Markup);

    _remote.RaiseDisplays(new DisplayList([new DisplayInfo(@"\\.\DISPLAY1", "Main", 0, 0, 800, 600, true)], @"\\.\DISPLAY1"));
    _remote.RaiseRegions(new ScreenRegions([new ScreenRegion(0, 0, 10, 10, [1, 2, 3], ImageEncoding.Jpeg)]));

    page.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "drawRegion"));
  }

  [Fact]
  public async Task ViewOnly_DoesNotCaptureInput_AndSaysSo()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false, SeeDesktop: true, ControlDesktop: false));
    await LoadAsync(_device);

    var page = Render<RemoteControlPage>(x => x.Add(p => p.Id, _device.Id));
    page.WaitForAssertion(() => Assert.True(_remote.Started));

    Assert.Contains("View only", page.Markup);
    Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "attachInput");
  }

  [Fact]
  public async Task TheClipboardButtons_FollowTheClipboardPermissions()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false, SeeDesktop: true, ControlDesktop: true, ReadClipboard: false, WriteClipboard: false));
    await LoadAsync(_device);

    var page = Render<RemoteControlPage>(x => x.Add(p => p.Id, _device.Id));
    page.WaitForAssertion(() => Assert.True(_remote.Started));

    Assert.DoesNotContain("Send clipboard", page.Markup);
    Assert.DoesNotContain("Get clipboard", page.Markup);
  }

  [Fact]
  public async Task SendClipboard_ReadsTheBrowsersClipboard_AndSendsIt()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false, SeeDesktop: true, ControlDesktop: true, WriteClipboard: true));
    JSInterop.Setup<string?>("readClipboard").SetResult("from the browser");
    await LoadAsync(_device);

    var page = Render<RemoteControlPage>(x => x.Add(p => p.Id, _device.Id));
    page.WaitForAssertion(() => Assert.True(_remote.Started));

    page.FindAll("button").First(b => b.TextContent.Contains("Send clipboard")).Click();

    page.WaitForAssertion(() => Assert.Contains("clipboard-set from the browser", _remote.Calls));
  }

  [Fact]
  public async Task GetClipboard_AsksTheDevice_AndWritesTheAnswerToTheBrowser()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false, SeeDesktop: true, ControlDesktop: true, ReadClipboard: true));
    JSInterop.Setup<bool>("writeClipboard", _ => true).SetResult(true);
    await LoadAsync(_device);

    var page = Render<RemoteControlPage>(x => x.Add(p => p.Id, _device.Id));
    page.WaitForAssertion(() => Assert.True(_remote.Started));

    page.FindAll("button").First(b => b.TextContent.Contains("Get clipboard")).Click();
    page.WaitForAssertion(() => Assert.Contains("clipboard-get", _remote.Calls));

    _remote.RaiseClipboardText("from the device");

    page.WaitForAssertion(() => Assert.Contains(
      JSInterop.Invocations,
      i => i.Identifier == "writeClipboard" && (string?)i.Arguments[0] == "from the device"));
  }

  [Fact]
  public async Task TheBlockInputButton_FollowsThePermission_AndTogglesOnClick()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false, SeeDesktop: true, ControlDesktop: true, BlockInput: false));
    await LoadAsync(_device);

    var page = Render<RemoteControlPage>(x => x.Add(p => p.Id, _device.Id));
    page.WaitForAssertion(() => Assert.True(_remote.Started));

    Assert.DoesNotContain("Block input", page.Markup);
  }

  [Fact]
  public async Task WithThePermission_TheBlockInputButton_TogglesAndShowsTheDevicesConfirmation()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false, SeeDesktop: true, ControlDesktop: true, BlockInput: true));
    await LoadAsync(_device);

    var page = Render<RemoteControlPage>(x => x.Add(p => p.Id, _device.Id));
    page.WaitForAssertion(() => Assert.True(_remote.Started));

    page.FindAll("button").First(b => b.TextContent == "Block input").Click();

    page.WaitForAssertion(() => Assert.Contains("blockinput True", _remote.Calls));
    Assert.Contains("Block input", page.Markup);

    _remote.RaiseBlockInput(true);
    page.WaitForAssertion(() => Assert.Contains("Unblock input", page.Markup));

    page.FindAll("button").First(b => b.TextContent == "Unblock input").Click();
    page.WaitForAssertion(() => Assert.Contains("blockinput False", _remote.Calls));
  }

  [Fact]
  public async Task ThePrivacyScreenButton_FollowsTheInteractPermission_AndTogglesOnClick()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false, SeeDesktop: true, ControlDesktop: false));
    await LoadAsync(_device);

    var page = Render<RemoteControlPage>(x => x.Add(p => p.Id, _device.Id));
    page.WaitForAssertion(() => Assert.True(_remote.Started));

    Assert.DoesNotContain("Privacy screen", page.Markup);
  }

  [Fact]
  public async Task WithInteract_ThePrivacyScreenButton_TogglesAndShowsTheDevicesConfirmation()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false, SeeDesktop: true, ControlDesktop: true));
    await LoadAsync(_device);

    var page = Render<RemoteControlPage>(x => x.Add(p => p.Id, _device.Id));
    page.WaitForAssertion(() => Assert.True(_remote.Started));

    page.FindAll("button").First(b => b.TextContent == "Privacy screen").Click();

    page.WaitForAssertion(() => Assert.Contains("privacyscreen True", _remote.Calls));

    _remote.RaisePrivacyScreen(true);
    page.WaitForAssertion(() => Assert.Contains("Hide privacy screen", page.Markup));

    page.FindAll("button").First(b => b.TextContent == "Hide privacy screen").Click();
    page.WaitForAssertion(() => Assert.Contains("privacyscreen False", _remote.Calls));
  }

  [Fact]
  public async Task ASecureScreen_IsShownToTheViewer_AndClearsWhenItGoesAway()
  {
    await LoadAsync(_device);
    var page = Render<RemoteControlPage>(x => x.Add(p => p.Id, _device.Id));
    page.WaitForAssertion(() => Assert.True(_remote.Started));

    _remote.RaiseSecureDesktop(new SecureDesktop(true, "Winlogon"));
    page.WaitForAssertion(() => Assert.Contains("secure screen", page.Markup));

    _remote.RaiseSecureDesktop(new SecureDesktop(false, null));
    page.WaitForAssertion(() => Assert.DoesNotContain("secure screen", page.Markup));
  }

  [Fact]
  public async Task WhenTheSessionEnds_ItSaysWhy()
  {
    await LoadAsync(_device);
    var page = Render<RemoteControlPage>(x => x.Add(p => p.Id, _device.Id));
    page.WaitForAssertion(() => Assert.True(_remote.Started));

    _remote.RaiseEnded("The other end left.");

    page.WaitForAssertion(() => Assert.Contains("The other end left.", page.Markup));
  }

  private Task LoadAsync(DeviceSummary device)
  {
    _api.RespondWith(new[] { device });
    return Services.GetRequiredService<DeviceStore>().LoadAsync();
  }
}
