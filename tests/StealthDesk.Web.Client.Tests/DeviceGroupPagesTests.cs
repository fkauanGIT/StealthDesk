using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.DeviceGroups;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.InstallerKeys;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.TenantSettings;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.Devices;
using StealthDesk.Web.Client.InstallerKeys;
using StealthDesk.Web.Client.TenantSettings;

namespace StealthDesk.Web.Client.Tests;

/// <summary>
/// Device groups in the browser: the list and a group's devices added, moved and removed; the device list filtered
/// by group; a device's groups on its page; and a key that puts new devices in a group.
/// </summary>
public class DeviceGroupPagesTests : AccountTestContext
{
  private static readonly Guid Tenant = Guid.NewGuid();
  private static readonly DateTimeOffset Created = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
  private static readonly Guid Sales = Guid.NewGuid();
  private static readonly Guid Hr = Guid.NewGuid();

  public DeviceGroupPagesTests()
  {
    Services.AddSingleton<SessionGuard>();
    Services.AddSingleton<DeviceStore>();
    Services.AddSingleton<ILiveUpdates>(new FakeLiveUpdates());
    Services.AddSingleton<IDeviceHub>(new FakeDeviceHub());
    Services.AddSingleton<InstallerKeyApi>();
    Services.AddSingleton<TenantSettingApi>();
    Services.AddSingleton(TimeProvider.System);
    JSInterop.Mode = JSRuntimeMode.Loose;
  }

  private static CurrentUser Me(params string[] permissions) => new()
  {
    Id = Guid.NewGuid(),
    Email = "admin@example.com",
    TenantId = Tenant,
    Permissions = [.. permissions],
  };

  private static DeviceGroupList Groups() =>
    new() { Items = [new DeviceGroupSummary(Hr, "HR", null, Created, 0), new DeviceGroupSummary(Sales, "Sales", "Sales floor", Created, 1)] };

  private static DeviceSummary Device(string name, params Guid[] groups) => SampleDevices.Sample(name) with { GroupIds = groups };

  [Fact]
  public void TheGroups_AreListedWithTheirDevices_AndANewOneOpens()
  {
    var created = new DeviceGroupDetail(Guid.NewGuid(), "Finance", null, Created, []);
    Api.RespondWith(Me(PermissionNames.TenantDeviceGroupsRead, PermissionNames.TenantDeviceGroupsWrite));
    Api.RespondWith(Groups());
    var page = RenderPage<Pages.DeviceGroups>();
    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody tr").Count));
    Assert.Equal("1", page.Find("tr[data-group='Sales'] [data-cell=members]").TextContent.Trim());

    Button(page, "New group").Click();
    Fill(page, "Name", "Finance");
    Api.Respond(HttpStatusCode.Created, created);
    page.Find(".sd-dialog form").Submit();

    page.WaitForAssertion(() => Assert.Equal($"device-groups/{created.Id}", Location));
    Assert.Equal($"POST {Routes.DeviceGroups}?tenantId={Tenant}", Api.Requests[2]);
  }

  [Fact]
  public void AGroupsDevices_AreAddedMovedAndRemoved()
  {
    var pc1 = Device("PC-1", Sales);
    var pc2 = Device("PC-2");
    Api.RespondWith(Me(PermissionNames.TenantDeviceGroupsRead, PermissionNames.DeviceGroupAssignDevices));
    Api.RespondWith(new DeviceGroupDetail(Sales, "Sales", null, Created, [new DeviceGroupDevice(pc1.Id, "PC-1", true, Created)]));
    var page = RenderGroup(Sales);
    page.WaitForAssertion(() => Assert.NotNull(page.Find("tr[data-device='PC-1']")));

    Api.RespondWith(new[] { pc1, pc2 });
    Button(page, "Add devices").Click();
    page.WaitForAssertion(() => Assert.Equal(["PC-2"], page.FindAll(".sd-member-picker label").Select(x => x.TextContent.Trim())));
    page.Find(".sd-member-picker input").Change(true);
    Api.Respond(HttpStatusCode.NoContent);
    Api.RespondWith(new DeviceGroupDetail(Sales, "Sales", null, Created, [new DeviceGroupDevice(pc1.Id, "PC-1", true, Created), new DeviceGroupDevice(pc2.Id, "PC-2", true, Created)]));
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Add").Click();
    page.WaitForAssertion(() => Assert.NotNull(page.Find("tr[data-device='PC-2']")));
    Assert.Equal($"POST {Routes.DeviceGroupMembers(Sales)}?tenantId={Tenant}", Api.Requests[3]);
    Assert.Contains($"\"deviceIds\":[\"{pc2.Id}\"]", Api.Bodies[3]);

    Api.RespondWith(Groups());
    page.Find("tr[data-device='PC-1']").QuerySelectorAll("button").Single(x => x.TextContent.Trim() == "Move").Click();
    page.WaitForAssertion(() => Assert.Equal(["HR"], page.FindAll(".sd-dialog select option").Select(x => x.TextContent)));
    Api.Respond(HttpStatusCode.NoContent);
    Api.RespondWith(new DeviceGroupDetail(Sales, "Sales", null, Created, [new DeviceGroupDevice(pc2.Id, "PC-2", true, Created)]));
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Move").Click();
    page.WaitForAssertion(() => Assert.Empty(page.FindAll("tr[data-device='PC-1']")));
    Assert.Equal($"POST {Routes.DeviceGroupMembers(Hr)}?tenantId={Tenant}", Api.Requests[6]);
    Assert.Contains($"\"fromGroupId\":\"{Sales}\"", Api.Bodies[6]);

    page.Find("tr[data-device='PC-2']").QuerySelectorAll("button").Single(x => x.TextContent.Trim() == "Remove").Click();
    Api.Respond(HttpStatusCode.NoContent);
    Api.RespondWith(new DeviceGroupDetail(Sales, "Sales", null, Created, []));
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Remove").Click();
    page.WaitForAssertion(() => Assert.Contains("No devices yet", page.Markup));
    Assert.Equal($"DELETE {Routes.DeviceGroupMembers(Sales)}?tenantId={Tenant}", Api.Requests[8]);
  }

  [Fact]
  public void OnlyReadingGroups_OffersNoChanges()
  {
    Api.RespondWith(Me(PermissionNames.TenantDeviceGroupsRead));
    Api.RespondWith(new DeviceGroupDetail(Sales, "Sales", null, Created, [new DeviceGroupDevice(Guid.NewGuid(), "PC-1", true, Created)]));

    var page = RenderGroup(Sales);

    page.WaitForAssertion(() => Assert.NotNull(page.Find("tr[data-device='PC-1']")));
    Assert.DoesNotContain(page.FindAll("button"), x => x.TextContent.Trim() is "Add devices" or "Move" or "Remove" or "Edit");
  }

  [Fact]
  public void TheDeviceList_FiltersByGroup()
  {
    Api.RespondWith(Me(PermissionNames.DeviceRead, PermissionNames.TenantDeviceGroupsRead));
    Api.RespondWith(new[] { Device("SALES-PC", Sales), Device("HR-PC", Hr), Device("LOOSE-PC") });
    Api.RespondWith(Groups());
    var page = RenderPage<Pages.Home>();
    page.WaitForAssertion(() => Assert.Equal(3, page.FindAll("tbody tr").Count));

    Input(page, "Group").Change(Sales.ToString());

    Assert.Equal(["SALES-PC"], page.FindAll("tbody tr a").Select(x => x.TextContent.Trim()));
  }

  [Fact]
  public void WithoutReadingGroups_NoFilterIsOffered()
  {
    Api.RespondWith(Me(PermissionNames.DeviceRead));
    Api.RespondWith(new[] { Device("SALES-PC", Sales) });

    var page = RenderPage<Pages.Home>();

    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));
    Assert.DoesNotContain(page.FindAll("label"), x => x.TextContent.Trim() == "Group");
    Assert.DoesNotContain(Api.Requests, x => x.Contains(Routes.DeviceGroups, StringComparison.Ordinal));
  }

  [Fact]
  public void ADevicesPage_NamesItsGroups()
  {
    var pc = Device("SALES-PC", Sales);
    Api.RespondWith(Me(PermissionNames.DeviceRead, PermissionNames.TenantDeviceGroupsRead));
    Api.RespondWith(pc);
    Api.RespondWith(Groups());

    var page = Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.DeviceDetails>(p => p.Add(c => c.Id, pc.Id)));

    page.WaitForAssertion(() => Assert.Equal("Sales", page.Find("[data-groups] a").TextContent));
    Assert.Equal($"device-groups/{Sales}", page.Find("[data-groups] a").GetAttribute("href"));
  }

  [Fact]
  public void ADevicesGroups_AreChangedFromItsPage_ASwapBeingAMove()
  {
    var pc = Device("SALES-PC", Sales);
    Api.RespondWith(Me(PermissionNames.DeviceRead, PermissionNames.TenantDeviceGroupsRead, PermissionNames.DeviceGroupAssignDevices));
    Api.RespondWith(pc);
    Api.RespondWith(Groups());
    var page = Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.DeviceDetails>(p => p.Add(c => c.Id, pc.Id)));
    page.WaitForAssertion(() => Assert.Equal("Sales", page.Find("[data-groups]").TextContent.Trim()));

    Button(page, "Change groups").Click();
    page.FindAll(".sd-member-picker label").Single(x => x.TextContent.Trim() == "Sales").QuerySelector("input")!.Change(false);
    page.FindAll(".sd-member-picker label").Single(x => x.TextContent.Trim() == "HR").QuerySelector("input")!.Change(true);
    Api.Respond(HttpStatusCode.NoContent);
    Api.RespondWith(pc with { GroupIds = [Hr] });
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Save").Click();

    page.WaitForAssertion(() => Assert.Equal("HR", page.Find("[data-groups]").TextContent.Trim()));
    Assert.Equal($"POST {Routes.DeviceGroupMembers(Hr)}?tenantId={Tenant}", Api.Requests[3]);
    Assert.Contains($"\"fromGroupId\":\"{Sales}\"", Api.Bodies[3]);
    Assert.Equal(5, Api.Requests.Count);
  }

  [Fact]
  public void AddingAndLeavingSeveral_AreAChangeOfEachGroup_AndARefusalIsShown()
  {
    var pc = Device("SALES-PC", Sales);
    Api.RespondWith(Me(PermissionNames.DeviceRead, PermissionNames.TenantDeviceGroupsRead, PermissionNames.DeviceGroupAssignDevices));
    Api.RespondWith(pc);
    Api.RespondWith(Groups());
    var page = Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.DeviceDetails>(p => p.Add(c => c.Id, pc.Id)));
    page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-groups] a")));

    Button(page, "Change groups").Click();
    page.FindAll(".sd-member-picker label").Single(x => x.TextContent.Trim() == "HR").QuerySelector("input")!.Change(true);
    Api.Respond(HttpStatusCode.Forbidden);
    Api.RespondWith(pc);
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Save").Click();

    page.WaitForAssertion(() => Assert.Contains("Your account can't do that.", page.Markup));
    Assert.Equal($"POST {Routes.DeviceGroupMembers(Hr)}?tenantId={Tenant}", Api.Requests[3]);
    Assert.DoesNotContain("fromGroupId\":\"", Api.Bodies[3]);
  }

  [Fact]
  public void WithoutAssigning_TheGroupsAreOnlyShown()
  {
    var pc = Device("LOOSE-PC");
    Api.RespondWith(Me(PermissionNames.DeviceRead, PermissionNames.TenantDeviceGroupsRead));
    Api.RespondWith(pc);
    Api.RespondWith(Groups());

    var page = Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.DeviceDetails>(p => p.Add(c => c.Id, pc.Id)));

    page.WaitForAssertion(() => Assert.Contains("In no group", page.Markup));
    Assert.DoesNotContain(page.FindAll("button"), x => x.TextContent.Trim() == "Change groups");
  }

  [Fact]
  public void AKey_CanPutNewDevicesInAGroup()
  {
    var key = new InstallerKey(Guid.NewGuid(), null, InstallerKeyType.Persistent, InstallerKeyCreatorKind.User, Guid.NewGuid(), "admin@example.com", Created, null, null, 0, Sales, "Sales");
    Api.RespondWith(Me(PermissionNames.AgentInstall, PermissionNames.InstallerKeyRead, PermissionNames.InstallerKeyWrite, PermissionNames.TenantDeviceGroupsRead));
    Api.RespondWith(new InstallerKeyList());
    Api.RespondWith(new DeploymentOptions(null));
    Api.RespondWith(Groups());
    var page = RenderPage<Pages.Deploy>();
    page.WaitForAssertion(() => Assert.Equal(["No group", "HR", "Sales"], Input(page, "Device group (optional)").QuerySelectorAll("option").Select(x => x.TextContent)));

    page.FindAll(".sd-segmented button").Single(x => x.TextContent.Trim() == "Persistent").Click();
    Input(page, "Device group (optional)").Change(Sales.ToString());
    Api.Respond(HttpStatusCode.Created, new CreatedInstallerKey(key, $"{key.Id}:x"));
    Api.RespondWith(Array.Empty<DeviceSummary>());
    page.Find("form.sd-deploy-form").Submit();

    page.WaitForAssertion(() => Assert.Contains("New devices join the group Sales.", page.Markup));
    Assert.Contains($"\"deviceGroupId\":\"{Sales}\"", Api.Bodies[4]);
  }

  [Fact]
  public void TheKeysPage_ShowsEachKeysGroup()
  {
    Api.RespondWith(Me(PermissionNames.InstallerKeyRead));
    Api.RespondWith(new InstallerKeyList
    {
      Items =
      [
        new InstallerKey(Guid.NewGuid(), "Sales PCs", InstallerKeyType.Persistent, InstallerKeyCreatorKind.User, Guid.NewGuid(), null, Created, null, null, 0, Sales, "Sales"),
        new InstallerKey(Guid.NewGuid(), "Any PC", InstallerKeyType.Persistent, InstallerKeyCreatorKind.User, Guid.NewGuid(), null, Created, null, null, 0),
      ],
    });

    var page = RenderPage<Pages.InstallerKeys>();

    page.WaitForAssertion(() => Assert.Equal("Sales", page.Find("tr[data-key='Sales PCs'] [data-cell=group]").TextContent.Trim()));
    Assert.Equal("None", page.Find("tr[data-key='Any PC'] [data-cell=group]").TextContent.Trim());
  }

  private IRenderedComponent<CascadingAuthenticationState> RenderPage<TPage>()
    where TPage : Microsoft.AspNetCore.Components.IComponent =>
    Render<CascadingAuthenticationState>(x => x.AddChildContent<TPage>());

  private IRenderedComponent<CascadingAuthenticationState> RenderGroup(Guid id) =>
    Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.DeviceGroupDetails>(p => p.Add(c => c.Id, id)));

  private static AngleSharp.Dom.IElement Button(IRenderedComponent<CascadingAuthenticationState> page, string text) =>
    page.FindAll("button").First(x => x.TextContent.Trim() == text);
}
