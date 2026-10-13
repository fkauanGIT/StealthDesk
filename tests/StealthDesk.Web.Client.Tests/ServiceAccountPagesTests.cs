using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.ServiceAccounts;
using StealthDesk.Contracts.UserGroups;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.Permissions;
using StealthDesk.Web.Client.PersonalAccessTokens;
using StealthDesk.Web.Client.ServiceAccounts;
using StealthDesk.Web.Client.UserGroups;
using StealthDesk.Web.Client.Users;

namespace StealthDesk.Web.Client.Tests;

/// <summary>
/// The service account pages: the accounts and their keys, creating one with its first key shown once, managing
/// keys, the server page's access modes, and picking service accounts on the permissions page.
/// </summary>
public class ServiceAccountPagesTests : AccountTestContext
{
  private static readonly Guid Tenant = Guid.NewGuid();
  private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

  private static readonly string[] Managing =
  [
    PermissionNames.ServiceAccountRead,
    PermissionNames.ServiceAccountWrite,
    PermissionNames.ServiceAccountRotateCredentials,
    PermissionNames.TenantPermissionsRead,
  ];

  public ServiceAccountPagesTests()
  {
    Services.AddSingleton<SessionGuard>();
    Services.AddSingleton<ServiceAccountApi>();
    Services.AddSingleton<PermissionApi>();
    Services.AddSingleton<PersonalAccessTokenApi>();
    Services.AddSingleton<UserGroupApi>();
    Services.AddSingleton<UserApi>();
    Services.AddSingleton<TimeProvider>(new FixedClock(Now));
    JSInterop.Mode = JSRuntimeMode.Loose;
  }

  private static CurrentUser Me(params string[] permissions) => new()
  {
    Id = Guid.NewGuid(),
    Email = "admin@example.com",
    TenantId = Tenant,
    Permissions = permissions,
  };

  private static ServiceAccountCredential Key(string name, DateTimeOffset? expires = null, DateTimeOffset? revoked = null) =>
    new(Guid.NewGuid(), name, Now.AddDays(-5), expires, revoked, null);

  private static ServiceAccount Account(string name, params ServiceAccountCredential[] keys) =>
    new(Guid.NewGuid(), name, "Pulls the inventory", ServiceAccountKind.Tenant, true, ServiceAccountAccessMode.Restricted, Now.AddDays(-10), keys);

  [Fact]
  public void TheList_CountsActiveKeys_AndOpensThemWithTheirStatus()
  {
    var glpi = Account("GLPI", Key("Active"), Key("Revoked", revoked: Now.AddDays(-1)), Key("Expired", expires: Now.AddMinutes(-1)));
    Api.RespondWith(Me(Managing));
    Api.RespondWith(new ServiceAccountList { Items = [glpi] });
    var page = RenderTenant();
    page.WaitForAssertion(() => Assert.Single(page.FindAll("tr[data-account]")));

    Assert.Equal($"GET {Routes.ServiceAccounts}?tenantId={Tenant}", Api.Requests[1]);
    var row = page.Find("tr[data-account='GLPI']");
    Assert.Contains("1 active of 3", row.TextContent);
    Assert.Contains("Pulls the inventory", row.TextContent);
    Assert.Empty(page.FindAll("tr[data-credential]"));

    row.QuerySelectorAll("button").Single(x => x.TextContent.Contains("active of")).Click();

    Assert.Contains("Active", page.Find("tr[data-credential='Active']").TextContent);
    Assert.Contains("Revoke", page.Find("tr[data-credential='Active']").TextContent);
    Assert.Contains("Revoked", page.Find("tr[data-credential='Revoked']").TextContent);
    Assert.Contains("Expired", page.Find("tr[data-credential='Expired']").TextContent);
    Assert.DoesNotContain("Revoke", page.Find("tr[data-credential='Expired'] .sd-row-actions").TextContent);
    Assert.Contains("Delete", page.Find("tr[data-credential='Expired'] .sd-row-actions").TextContent);
  }

  [Fact]
  public void Creating_WithAFirstKey_ShowsItOnce_ThenOpensTheAccountsPermissions()
  {
    var glpi = Account("GLPI");
    var key = Key("Primary", expires: Now.AddDays(90));
    Api.RespondWith(Me(Managing));
    Api.RespondWith(new ServiceAccountList());
    var page = RenderTenant();
    page.WaitForAssertion(() => Assert.Contains("No service accounts", page.Markup));

    Button(page, "New account").Click();
    Fill(page, "Name", "GLPI");
    Fill(page, "Description", "Pulls the inventory");
    Api.Respond(HttpStatusCode.Created, glpi);
    Api.Respond(HttpStatusCode.Created, new CreatedServiceAccountCredential(key, $"{key.Id}:shown-once"));
    Api.RespondWith(new ServiceAccountList { Items = [glpi with { Credentials = [key] }] });
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Create").Click();

    page.WaitForAssertion(() => Assert.Equal($"{key.Id}:shown-once", page.Find("[data-secret]").TextContent));
    Assert.Equal($"POST {Routes.ServiceAccounts}?tenantId={Tenant}", Api.Requests[2]);
    Assert.Contains("\"name\":\"GLPI\"", Api.Bodies[2]);
    Assert.Contains("\"description\":\"Pulls the inventory\"", Api.Bodies[2]);
    Assert.Equal($"POST {Routes.ServiceAccountCredentials(Routes.ServiceAccounts, glpi.Id)}?tenantId={Tenant}", Api.Requests[3]);
    Assert.Contains("\"name\":\"Primary\"", Api.Bodies[3]);
    Assert.Contains($"\"expiresAt\":\"{Now.AddDays(90):yyyy-MM-ddTHH:mm:ss}", Api.Bodies[3]);

    Button(page, "Copy key").Click();
    Assert.Equal($"{key.Id}:shown-once", JSInterop.Invocations["navigator.clipboard.writeText"].Single().Arguments[0]);
    Button(page, "Done").Click();

    page.WaitForAssertion(() => Assert.Equal($"permissions?kind=ServiceAccount&id={glpi.Id}", Location));
  }

  [Fact]
  public void Keys_AreAdded_Revoked_AndDeleted_EachAskingFirst()
  {
    var active = Key("Old");
    var dead = Key("Older", revoked: Now.AddDays(-2));
    var glpi = Account("GLPI", active, dead);
    var added = Key("New", expires: Now.AddDays(30));
    Api.RespondWith(Me(Managing));
    Api.RespondWith(new ServiceAccountList { Items = [glpi] });
    var page = RenderTenant();
    page.WaitForAssertion(() => Assert.Single(page.FindAll("tr[data-account]")));

    page.FindAll("tr[data-account='GLPI'] button").Single(x => x.TextContent.Trim() == "Add key").Click();
    Fill(page, "Key name", "New");
    page.Find(".sd-dialog select").Change("30");
    Api.Respond(HttpStatusCode.Created, new CreatedServiceAccountCredential(added, $"{added.Id}:new-secret"));
    Api.RespondWith(new ServiceAccountList { Items = [glpi with { Credentials = [active, dead, added] }] });
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Add key").Click();
    page.WaitForAssertion(() => Assert.Equal($"{added.Id}:new-secret", page.Find("[data-secret]").TextContent));
    Assert.Contains($"\"expiresAt\":\"{Now.AddDays(30):yyyy-MM-ddTHH:mm:ss}", Api.Bodies[2]);
    Button(page, "Done").Click();
    Assert.Equal(string.Empty, Location);

    page.WaitForAssertion(() => Assert.NotNull(page.Find("tr[data-credential='Old']")));
    page.FindAll("tr[data-credential='Old'] button").Single(x => x.TextContent.Trim() == "Revoke").Click();
    Assert.Contains("The account's other keys keep working", page.Find(".sd-dialog").TextContent);
    Api.Respond(HttpStatusCode.NoContent);
    Api.RespondWith(new ServiceAccountList { Items = [glpi] });
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Revoke").Click();
    page.WaitForAssertion(() => Assert.Equal($"DELETE {Routes.ServiceAccountCredential(Routes.ServiceAccounts, glpi.Id, active.Id)}?tenantId={Tenant}", Api.Requests[4]));

    page.WaitForAssertion(() => Assert.NotNull(page.Find("tr[data-credential='Older']")));
    page.FindAll("tr[data-credential='Older'] button").Single(x => x.TextContent.Trim() == "Delete").Click();
    Api.Respond(HttpStatusCode.NoContent);
    Api.RespondWith(new ServiceAccountList { Items = [glpi] });
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Delete").Click();
    page.WaitForAssertion(() => Assert.Equal($"DELETE {Routes.ServiceAccountCredentialPurge(Routes.ServiceAccounts, glpi.Id, dead.Id)}?tenantId={Tenant}", Api.Requests[6]));
  }

  [Fact]
  public void Disabling_SendsTheChange_AndAServersRefusalIsShown()
  {
    var glpi = Account("GLPI");
    Api.RespondWith(Me(Managing));
    Api.RespondWith(new ServiceAccountList { Items = [glpi] });
    var page = RenderTenant();
    page.WaitForAssertion(() => Assert.Single(page.FindAll("tr[data-account]")));

    Api.RespondWith(glpi with { IsEnabled = false });
    Api.RespondWith(new ServiceAccountList { Items = [glpi with { IsEnabled = false }] });
    page.Find("tr[data-account='GLPI'] input[type=checkbox]").Change(false);
    page.WaitForAssertion(() => Assert.Contains("Off", page.Find("tr[data-account='GLPI']").TextContent));
    Assert.Equal($"PUT {Routes.ServiceAccount(Routes.ServiceAccounts, glpi.Id)}?tenantId={Tenant}", Api.Requests[2]);
    Assert.Contains("\"isEnabled\":false", Api.Bodies[2]);

    Button(page, "New account").Click();
    Fill(page, "Name", "GLPI");
    Api.Respond(HttpStatusCode.Conflict, new { detail = "A service account with that name already exists in this tenant." });
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Create").Click();
    page.WaitForAssertion(() => Assert.Equal("A service account with that name already exists in this tenant.", ErrorOf(page, "Name").TextContent));
  }

  [Fact]
  public void ReadingOnly_ShowsTheAccounts_WithNothingToChangeThem()
  {
    Api.RespondWith(Me(PermissionNames.ServiceAccountRead));
    Api.RespondWith(new ServiceAccountList { Items = [Account("GLPI", Key("Active"))] });

    var page = RenderTenant();

    page.WaitForAssertion(() => Assert.Single(page.FindAll("tr[data-account]")));
    Assert.DoesNotContain(page.FindAll("button"), x => x.TextContent.Trim() is "New account" or "Edit" or "Delete" or "Add key" or "Revoke");
    Assert.True(page.Find("tr[data-account='GLPI'] input[type=checkbox]").HasAttribute("disabled"));
    Assert.DoesNotContain("Permissions", page.Find("tr[data-account='GLPI']").TextContent);
  }

  [Fact]
  public void TheServerPage_OffersUnrestricted_OnlyToWhoManagesTheServersPermissions()
  {
    string[] server = [PermissionNames.ServerServiceAccountsRead, PermissionNames.ServerServiceAccountsWrite];
    Api.RespondWith(Me(server));
    Api.RespondWith(new ServiceAccountList());
    var page = Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.ServerServiceAccounts>());
    page.WaitForAssertion(() => Assert.Contains("No service accounts", page.Markup));
    Assert.Equal($"GET {Routes.ServerServiceAccounts}", Api.Requests[1]);

    Button(page, "New account").Click();
    Fill(page, "Name", "Root");
    page.FindAll(".sd-segmented button").Single(x => x.TextContent.Trim() == "Unrestricted").Click();
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Create").Click();

    Assert.Equal("Your account can't create unrestricted accounts.", ErrorOf(page, "Name").TextContent);
    Assert.Equal(2, Api.Requests.Count);
    Assert.Contains("can't issue keys", page.Find(".sd-dialog").TextContent);
  }

  [Fact]
  public void TheServerPage_CreatesAnUnrestrictedAccount_ForTheServersPermissionManager()
  {
    var root = Account("Root") with { Kind = ServiceAccountKind.Server, AccessMode = ServiceAccountAccessMode.Unrestricted };
    Api.RespondWith(Me(PermissionNames.ServerServiceAccountsRead, PermissionNames.ServerServiceAccountsWrite, PermissionNames.ServerPermissionsWrite));
    Api.RespondWith(new ServiceAccountList());
    var page = Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.ServerServiceAccounts>());
    page.WaitForAssertion(() => Assert.Contains("No service accounts", page.Markup));

    Button(page, "New account").Click();
    Fill(page, "Name", "Root");
    page.FindAll(".sd-segmented button").Single(x => x.TextContent.Trim() == "Unrestricted").Click();
    Api.Respond(HttpStatusCode.Created, root);
    Api.RespondWith(new ServiceAccountList { Items = [root] });
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Create").Click();

    page.WaitForAssertion(() => Assert.Contains("Unrestricted", page.Find("tr[data-account='Root']").TextContent));
    Assert.Equal($"POST {Routes.ServerServiceAccounts}", Api.Requests[2]);
    Assert.Contains($"\"accessMode\":{(int)ServiceAccountAccessMode.Unrestricted}", Api.Bodies[2]);
  }

  [Fact]
  public void ThePermissionsPage_PicksTheTenantsAccounts_AndTheServersForWhoMaySeeThem()
  {
    var glpi = Account("GLPI");
    var root = Account("Root") with { Kind = ServiceAccountKind.Server };
    Navigation.NavigateTo($"permissions?kind=ServiceAccount&id={glpi.Id}");
    Api.RespondWith(Me(PermissionNames.TenantPermissionsRead, PermissionNames.ServerServiceAccountsRead));
    Api.RespondWith(new ServiceAccountList { Items = [glpi] });
    Api.RespondWith(new ServiceAccountList { Items = [root] });
    Api.RespondWith(new PermissionCatalogList());
    Api.RespondWith(new PermissionPresetList());
    Api.RespondWith(new List<DeviceSummary>());
    Api.RespondWith(new UserGroupList());
    Api.RespondWith(new PermissionAssignmentList());

    var page = Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.Permissions>());

    page.WaitForAssertion(() => Assert.Contains($"principalKind=ServiceAccount&principalId={glpi.Id}", string.Join(" ", Api.Requests)));
    Assert.Equal($"GET {Routes.ServiceAccounts}?tenantId={Tenant}", Api.Requests[1]);
    Assert.Equal($"GET {Routes.ServerServiceAccounts}", Api.Requests[2]);
    var options = page.FindAll("select option").Select(x => x.TextContent.Trim()).ToList();
    Assert.Contains("Choose a service account", options);
    Assert.Contains("GLPI", options);
    Assert.Contains("Root · server", options);
  }

  [Fact]
  public void ThePermissionsPage_ListsOnlyTheTenantsAccounts_ToATenantAdministrator()
  {
    var glpi = Account("GLPI");
    Navigation.NavigateTo("permissions?kind=ServiceAccount");
    Api.RespondWith(Me(PermissionNames.TenantPermissionsRead));
    Api.RespondWith(new ServiceAccountList { Items = [glpi] });

    var page = Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.Permissions>());

    page.WaitForAssertion(() => Assert.Contains("GLPI", page.FindAll("select option").Select(x => x.TextContent.Trim())));
    Assert.DoesNotContain(Api.Requests, x => x.Contains(Routes.ServerServiceAccounts));
  }

  private IRenderedComponent<CascadingAuthenticationState> RenderTenant() =>
    Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.ServiceAccounts>());

  private static AngleSharp.Dom.IElement Button(IRenderedComponent<CascadingAuthenticationState> page, string text) =>
    page.FindAll("button").First(x => x.TextContent.Trim() == text);

  private sealed class FixedClock(DateTimeOffset now) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => now;
  }
}
