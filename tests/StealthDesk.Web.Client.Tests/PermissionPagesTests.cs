using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.UserGroups;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.Permissions;
using StealthDesk.Web.Client.UserGroups;
using StealthDesk.Web.Client.Users;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The permissions panel: a principal's assignments, granting, changing, removing, presets and checks.</summary>
public class PermissionPagesTests : AccountTestContext
{
  private static readonly Guid Tenant = Guid.NewGuid();
  private static readonly Guid Tech = Guid.NewGuid();
  private static readonly DateTimeOffset Created = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

  private static readonly PermissionCatalogList Catalog = new()
  {
    Items =
    [
      new(PermissionNames.DeviceRead, "Devices", "Read devices", "View devices.", [PermissionScopeKind.Device, PermissionScopeKind.Tenant, PermissionScopeKind.Server]),
      new(PermissionNames.TenantUsersRead, "Tenant", "Read users", "View the tenant's users.", [PermissionScopeKind.Tenant]),
    ],
  };

  private static readonly PermissionPresetList Presets = new() { Items = [new(PermissionPresets.SelfService, [PermissionNames.PersonalAccessTokenSelfRead])] };

  public PermissionPagesTests()
  {
    Services.AddSingleton<SessionGuard>();
    Services.AddSingleton<PermissionApi>();
    Services.AddSingleton<UserGroupApi>();
    Services.AddSingleton<UserApi>();
  }

  private static CurrentUser Me(params string[] permissions) => new()
  {
    Id = Guid.NewGuid(),
    Email = "admin@example.com",
    TenantId = Tenant,
    Permissions = [PermissionNames.TenantPermissionsRead, .. permissions],
  };

  private static PermissionAssignment Assignment(string permission, PermissionEffect effect = PermissionEffect.Allow, bool enabled = true) =>
    new(Guid.NewGuid(), PermissionPrincipalKind.User, Tech, permission, effect, PermissionScopeKind.Tenant, Tenant, "Why", enabled, Created);

  // The panel asks for, in order: the catalog, the presets, the devices, the groups, then the assignments.
  private void RespondToOpening(CurrentUser me, params PermissionAssignment[] assignments)
  {
    Api.RespondWith(me);
    Api.RespondWith(Catalog);
    Api.RespondWith(Presets);
    Api.RespondWith(new List<DeviceSummary> { new() { Id = Guid.NewGuid(), Name = "PC-FIN" } });
    Api.RespondWith(new UserGroupList { Items = [new UserGroupSummary(Guid.NewGuid(), "Support", null, Created, 1)] });
    Api.RespondWith(new PermissionAssignmentList { Items = assignments });
  }

  [Fact]
  public void Assignments_ShowWhatWhereAndWhetherOn()
  {
    RespondToOpening(Me(), Assignment(PermissionNames.TenantUsersRead), Assignment(PermissionNames.DeviceRead, PermissionEffect.Deny, enabled: false));

    var panel = RenderPanel();

    panel.WaitForAssertion(() => Assert.Equal(2, panel.FindAll("tbody tr").Count));
    var deny = panel.Find("tr[data-assignment='device.read']");
    Assert.Contains("Read devices", deny.TextContent);
    Assert.Contains("Deny", deny.TextContent);
    Assert.Contains("This tenant", deny.QuerySelector("[data-cell=scope]")!.TextContent);
    Assert.Contains("Off", deny.TextContent);
    Assert.Contains($"principalKind=User&principalId={Tech}", Api.Requests[5]);
  }

  [Fact]
  public void WithoutTheWritePermission_NothingCanBeChanged()
  {
    RespondToOpening(Me(), Assignment(PermissionNames.TenantUsersRead));

    var panel = RenderPanel();

    panel.WaitForAssertion(() => Assert.Single(panel.FindAll("tbody tr")));
    Assert.DoesNotContain(panel.FindAll("button"), x => x.TextContent.Trim() is "Add permission" or "Edit" or "Apply");
    Assert.Empty(panel.FindAll("tbody input"));
  }

  [Fact]
  public void Adding_SendsThePermissionScopeAndNotes_ThenReloads()
  {
    RespondToOpening(Me(PermissionNames.TenantPermissionsWrite));
    var panel = RenderPanel();
    panel.WaitForAssertion(() => Assert.Contains("No permissions", panel.Markup));

    Button(panel, "Add permission").Click();
    Field(panel, "Permission").Change(PermissionNames.DeviceRead);
    Assert.Equal("View devices.", panel.Find("[data-permission-description]").TextContent);
    // Without the right to deny, only allow is offered; without the server's, no server scope.
    Assert.Equal(["Allow"], Field(panel, "Effect").QuerySelectorAll("option").Select(x => x.TextContent));
    Assert.DoesNotContain("The server", Field(panel, "Scope").TextContent);
    Field(panel, "Scope").Change(nameof(PermissionScopeKind.Device));
    var device = Field(panel, "Device").QuerySelectorAll("option")[1].GetAttribute("value")!;
    Field(panel, "Device").Change(device);
    Field(panel, "Notes").Change("Finance PC");
    Api.Respond(HttpStatusCode.Created, Assignment(PermissionNames.DeviceRead));
    Api.RespondWith(new PermissionAssignmentList { Items = [Assignment(PermissionNames.DeviceRead)] });
    Button(panel, "Add").Click();

    panel.WaitForAssertion(() => Assert.Single(panel.FindAll("tbody tr")));
    Assert.Equal($"POST {Routes.PermissionAssignments}?tenantId={Tenant}", Api.Requests[6]);
    Assert.Contains("\"permission\":\"device.read\"", Api.Bodies[6]);
    Assert.Contains($"\"scopeId\":\"{device}\"", Api.Bodies[6]);
    Assert.Contains("\"notes\":\"Finance PC\"", Api.Bodies[6]);
    Assert.Empty(panel.FindAll(".sd-dialog"));
  }

  [Fact]
  public void ADevicePermission_CanBeGrantedOnADeviceGroup()
  {
    var sales = Guid.NewGuid();
    var catalog = new PermissionCatalogList
    {
      Items = [new(PermissionNames.DeviceRead, "Devices", "Read devices", "View devices.", [PermissionScopeKind.Device, PermissionScopeKind.DeviceGroup, PermissionScopeKind.Tenant])],
    };
    Api.RespondWith(Me(PermissionNames.TenantPermissionsWrite, PermissionNames.TenantDeviceGroupsRead));
    Api.RespondWith(catalog);
    Api.RespondWith(Presets);
    Api.RespondWith(new List<DeviceSummary>());
    Api.RespondWith(new UserGroupList());
    Api.RespondWith(new StealthDesk.Contracts.DeviceGroups.DeviceGroupList { Items = [new(sales, "Sales", null, Created, 3)] });
    Api.RespondWith(new PermissionAssignmentList());
    var panel = RenderPanel();
    panel.WaitForAssertion(() => Assert.Contains("No permissions", panel.Markup));

    Button(panel, "Add permission").Click();
    Field(panel, "Permission").Change(PermissionNames.DeviceRead);
    Assert.Contains("One device group", Field(panel, "Scope").TextContent);
    Field(panel, "Scope").Change(nameof(PermissionScopeKind.DeviceGroup));
    Assert.Equal(["Choose a device group", "Sales"], Field(panel, "Device group").QuerySelectorAll("option").Select(x => x.TextContent));
    Button(panel, "Add").Click();
    panel.WaitForAssertion(() => Assert.Contains("Choose a device group.", panel.Find(".sd-dialog").TextContent));
    Field(panel, "Device group").Change(sales.ToString());
    var granted = new PermissionAssignment(Guid.NewGuid(), PermissionPrincipalKind.User, Tech, PermissionNames.DeviceRead, PermissionEffect.Allow, PermissionScopeKind.DeviceGroup, sales, null, true, Created);
    Api.Respond(HttpStatusCode.Created, granted);
    Api.RespondWith(new PermissionAssignmentList { Items = [granted] });
    Button(panel, "Add").Click();

    panel.WaitForAssertion(() => Assert.Contains("Device group · Sales", panel.Find("[data-cell=scope]").TextContent));
    Assert.Contains($"\"scopeKind\":{(int)PermissionScopeKind.DeviceGroup}", Api.Bodies[7]);
    Assert.Contains($"\"scopeId\":\"{sales}\"", Api.Bodies[7]);
  }

  [Fact]
  public void ARefusal_IsExplainedInTheForm()
  {
    RespondToOpening(Me(PermissionNames.TenantPermissionsWrite, PermissionNames.TenantPermissionsDeny));
    var panel = RenderPanel();
    panel.WaitForAssertion(() => Assert.Contains("No permissions", panel.Markup));

    Button(panel, "Add permission").Click();
    Field(panel, "Permission").Change(PermissionNames.TenantUsersRead);
    Field(panel, "Effect").Change(nameof(PermissionEffect.Deny));
    Api.Respond(HttpStatusCode.BadRequest, new { detail = "You can't remove 'Manage permissions' from yourself: you need it to keep managing permissions." });
    Button(panel, "Add").Click();

    panel.WaitForAssertion(() => Assert.Contains("You can't remove 'Manage permissions' from yourself", panel.Find(".sd-dialog").TextContent));
  }

  [Fact]
  public void Toggling_Editing_AndDeletingSelected_ReachTheServer()
  {
    var read = Assignment(PermissionNames.TenantUsersRead);
    RespondToOpening(Me(PermissionNames.TenantPermissionsWrite), read);
    var panel = RenderPanel();
    panel.WaitForAssertion(() => Assert.Single(panel.FindAll("tbody tr")));

    Api.RespondWith(read with { IsEnabled = false });
    Api.RespondWith(new PermissionAssignmentList { Items = [read with { IsEnabled = false }] });
    panel.FindAll("tbody tr input[type=checkbox]")[1].Change(false);
    panel.WaitForAssertion(() => Assert.Contains("Off", panel.Find("tbody tr").TextContent));
    Assert.Equal($"PUT {Routes.PermissionAssignment(read.Id)}?tenantId={Tenant}", Api.Requests[6]);
    Assert.Contains("\"isEnabled\":false", Api.Bodies[6]);

    panel.FindAll("tbody tr input[type=checkbox]")[0].Change(true);
    Button(panel, "Delete 1").Click();
    Api.RespondWith(new DeletePermissionAssignmentsResult([read.Id], []));
    Api.RespondWith(new PermissionAssignmentList());
    panel.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Delete").Click();

    panel.WaitForAssertion(() => Assert.Contains("No permissions", panel.Markup));
    Assert.Equal($"POST {Routes.PermissionAssignmentsBatchDelete}?tenantId={Tenant}", Api.Requests[8]);
    Assert.Contains(read.Id.ToString(), Api.Bodies[8]);
  }

  [Fact]
  public void Presets_AreAppliedInTheChosenMode()
  {
    RespondToOpening(Me(PermissionNames.TenantPermissionsWrite));
    var panel = RenderPanel();
    panel.WaitForAssertion(() => Assert.Contains("Apply presets", panel.Markup));

    panel.FindAll(".sd-preset-picker input")[0].Change(true);
    Input(panel, "Mode").Change("replace");
    Api.RespondWith(1);
    Api.RespondWith(new PermissionAssignmentList { Items = [Assignment(PermissionNames.PersonalAccessTokenSelfRead)] });
    Button(panel, "Apply").Click();

    panel.WaitForAssertion(() => Assert.Contains("1 permission granted.", panel.Markup));
    Assert.Equal($"POST {Routes.ApplyPermissionPresets}?tenantId={Tenant}", Api.Requests[6]);
    Assert.Contains("\"presets\":[\"Self Service\"]", Api.Bodies[6]);
    Assert.Contains("\"replaceExisting\":true", Api.Bodies[6]);
  }

  [Fact]
  public void Checking_SaysWhetherAllowed_AndWhy()
  {
    RespondToOpening(Me());
    var panel = RenderPanel();
    panel.WaitForAssertion(() => Assert.Contains("Check a permission", panel.Markup));

    Input(panel, "Permission").Change(PermissionNames.TenantUsersRead);
    Api.RespondWith(new EffectivePermission(true, "Allowed by a user group assignment at Tenant scope."));
    Button(panel, "Check").Click();

    panel.WaitForAssertion(() => Assert.Contains("Allowed by a user group assignment at Tenant scope.", panel.Markup));
    Assert.StartsWith($"GET {Routes.EffectivePermission(Tech)}?tenantId={Tenant}&principalKind=User&permission=tenant.users.read&scopeKind=Tenant", Api.Requests[6]);
  }

  private IRenderedComponent<CascadingAuthenticationState> RenderPanel() =>
    Render<CascadingAuthenticationState>(x => x.AddChildContent<PermissionAssignmentsPanel>(p => p
      .Add(c => c.TenantId, Tenant)
      .Add(c => c.Kind, PermissionPrincipalKind.User)
      .Add(c => c.PrincipalId, Tech)));

  // The form's own field, not the one with the same label on the page behind it.
  private static AngleSharp.Dom.IElement Field(IRenderedComponent<CascadingAuthenticationState> page, string label)
  {
    var dialog = page.Find(".sd-dialog");
    var labelElement = dialog.QuerySelectorAll("label").Single(x => x.TextContent.Trim() == label);
    return dialog.QuerySelector($"#{labelElement.GetAttribute("for")}")!;
  }

  private static AngleSharp.Dom.IElement Button(IRenderedComponent<CascadingAuthenticationState> page, string text) =>
    page.FindAll("button").First(x => x.TextContent.Trim() == text);
}
