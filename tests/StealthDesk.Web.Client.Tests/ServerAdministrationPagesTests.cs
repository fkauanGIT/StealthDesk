using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.ServerSettings;
using StealthDesk.Contracts.Tenants;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.AuthorizationLogs;
using StealthDesk.Web.Client.Layout;
using StealthDesk.Web.Client.ServerAdministration;

namespace StealthDesk.Web.Client.Tests;

/// <summary>
/// Server administration in the browser: the tenants page, the server settings with the alert and the test email,
/// the alert every page shows, and the server log reading the server or a tenant.
/// </summary>
public class ServerAdministrationPagesTests : AccountTestContext
{
  private static readonly Guid Own = Guid.NewGuid();
  private static readonly DateTimeOffset Created = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

  public ServerAdministrationPagesTests()
  {
    Services.AddSingleton<SessionGuard>();
    Services.AddSingleton<ServerAdministrationApi>();
    Services.AddSingleton<AuthorizationLogApi>();
    JSInterop.Mode = JSRuntimeMode.Loose;
  }

  private static CurrentUser Me(params string[] permissions) => new()
  {
    Id = Guid.NewGuid(),
    Email = "admin@example.com",
    TenantId = Own,
    Permissions = [.. permissions],
  };

  private static TenantList Tenants(params TenantSummary[] others) =>
    new() { Items = [new TenantSummary(Own, "Head office", Created, 3, 12), .. others] };

  [Fact]
  public void TheTenants_AreListed_YoursCantBeDeleted_AndOthersOnlyByTypingTheirName()
  {
    var branch = new TenantSummary(Guid.NewGuid(), "Branch", Created, 1, 2);
    Api.RespondWith(Me(PermissionNames.ServerTenantsRead, PermissionNames.ServerTenantsWrite, PermissionNames.ServerTenantsDelete));
    Api.RespondWith(Tenants(branch));
    var page = RenderPage<Pages.ServerTenants>();
    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody tr").Count));

    var own = page.Find("tr[data-tenant='Head office']");
    Assert.Contains("Yours", own.TextContent);
    Assert.Contains("12", own.TextContent);
    Assert.DoesNotContain(own.QuerySelectorAll("button"), x => x.TextContent.Trim() == "Delete");

    page.Find("tr[data-tenant='Branch']").QuerySelectorAll("button").Single(x => x.TextContent.Trim() == "Delete").Click();
    Assert.True(Button(page, "Delete tenant").HasAttribute("disabled"));
    Fill(page, "Type the tenant's name to confirm", "Branc");
    Assert.True(Button(page, "Delete tenant").HasAttribute("disabled"));
    Fill(page, "Type the tenant's name to confirm", "Branch");
    Api.Respond(HttpStatusCode.NoContent);
    Api.RespondWith(Tenants());
    Button(page, "Delete tenant").Click();

    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));
    Assert.Equal($"DELETE {Routes.Tenant(branch.Id)}", Api.Requests[2]);
  }

  [Fact]
  public void ATenant_IsCreatedAndRenamed()
  {
    var branch = new TenantSummary(Guid.NewGuid(), "Branch", Created, 0, 0);
    Api.RespondWith(Me(PermissionNames.ServerTenantsRead, PermissionNames.ServerTenantsWrite));
    Api.RespondWith(Tenants());
    var page = RenderPage<Pages.ServerTenants>();
    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));
    Assert.DoesNotContain(page.FindAll("tbody button"), x => x.TextContent.Trim() == "Delete");

    Fill(page, "New tenant", " Branch ");
    Api.Respond(HttpStatusCode.Created, branch);
    Api.RespondWith(Tenants(branch));
    page.Find("form.sd-token-form").Submit();
    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody tr").Count));
    Assert.Equal($"POST {Routes.Tenants}", Api.Requests[2]);
    Assert.Contains("\"name\":\"Branch\"", Api.Bodies[2]);

    page.Find("tr[data-tenant='Branch']").QuerySelectorAll("button").Single(x => x.TextContent.Trim() == "Rename").Click();
    Fill(page, "Name", "Branch 2");
    Api.Respond(HttpStatusCode.OK, branch with { Name = "Branch 2" });
    Api.RespondWith(Tenants(branch with { Name = "Branch 2" }));
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Rename").Click();

    page.WaitForAssertion(() => Assert.NotNull(page.Find("tr[data-tenant='Branch 2']")));
    Assert.Equal($"PUT {Routes.Tenant(branch.Id)}", Api.Requests[4]);
  }

  [Fact]
  public void OnlyReadingTenants_OffersNoChanges()
  {
    Api.RespondWith(Me(PermissionNames.ServerTenantsRead));
    Api.RespondWith(Tenants(new TenantSummary(Guid.NewGuid(), "Branch", Created, 0, 0)));

    var page = RenderPage<Pages.ServerTenants>();

    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody tr").Count));
    Assert.Empty(page.FindAll("form"));
    Assert.Empty(page.FindAll("tbody button"));
  }

  [Fact]
  public void TheAlert_IsPreviewedAndSaved()
  {
    Api.RespondWith(Me(PermissionNames.ServerSettingsWrite));
    Api.Respond(HttpStatusCode.NoContent);
    var page = RenderPage<Pages.ServerSettings>();
    page.WaitForAssertion(() => Assert.NotNull(page.Find("form")));

    page.Find("form").Submit();
    page.WaitForAssertion(() => Assert.Contains("Write the message to show", page.Markup));

    Fill(page, "Message", "Maintenance at 18:00");
    page.FindAll(".sd-segmented button").Single(x => x.TextContent.Trim() == "Warning").Click();
    Assert.Contains("Maintenance at 18:00", page.Find("[data-alert-preview] .sd-alert--warning").TextContent);
    Api.Respond(HttpStatusCode.OK, new ServerAlert("Maintenance at 18:00", AlertSeverity.Warning, true, false, true));
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("The alert is shown to everyone.", page.Markup));
    Assert.Equal($"PUT {Routes.ServerAlert}", Api.Requests[2]);
    Assert.Contains("\"message\":\"Maintenance at 18:00\"", Api.Bodies[2]);
    Assert.Contains($"\"severity\":{(int)AlertSeverity.Warning}", Api.Bodies[2]);
    Assert.Contains("\"isDismissible\":true", Api.Bodies[2]);
    Assert.Contains("\"comesBack\":false", Api.Bodies[2]);
  }

  [Fact]
  public void TheTestEmail_SaysWhereItWent_OrWhyNot()
  {
    Api.RespondWith(Me(PermissionNames.ServerSettingsWrite));
    Api.RespondWith(new ServerAlert("Old", AlertSeverity.Info, false, false, false));
    var page = RenderPage<Pages.ServerSettings>();
    page.WaitForAssertion(() => Assert.Equal("Old", Input(page, "Message").GetAttribute("value")));

    Api.Respond(HttpStatusCode.OK, new TestEmailResult("admin@example.com", "Sent."));
    Button(page, "Send test email").Click();
    page.WaitForAssertion(() => Assert.Contains("Sent. To: admin@example.com", page.Markup));
    Assert.Equal($"POST {Routes.ServerTestEmail}", Api.Requests[2]);

    Api.Respond(HttpStatusCode.BadGateway, new { detail = "The message couldn't be sent: no route to host." });
    Button(page, "Send test email").Click();
    page.WaitForAssertion(() => Assert.Contains("no route to host", page.Markup));
  }

  [Fact]
  public void TheAlert_IsShownOnEveryPage_UntilClosed()
  {
    Alerts.Current = new ServerAlert("Maintenance at 18:00", AlertSeverity.Warning, IsDismissible: true, ComesBack: false, IsEnabled: true);

    var banner = Render<ServerAlertBanner>();

    banner.WaitForAssertion(() => Assert.Contains("Maintenance at 18:00", banner.Find("[data-server-alert]").TextContent));
    Assert.Contains("sd-alert--warning", banner.Find("[data-server-alert]").ClassName);
    banner.Find("[data-server-alert] button").Click();
    Assert.Empty(banner.FindAll("[data-server-alert]"));
    var stored = JSInterop.Invocations["localStorage.setItem"].Single().Arguments;
    Assert.Equal("Warning:Maintenance at 18:00", stored[1]);
  }

  [Fact]
  public void ANewAlert_ShowsOnTheNextPage_WithoutAReload()
  {
    var banner = Render<ServerAlertBanner>();
    Assert.Empty(banner.FindAll("[data-server-alert]"));

    Alerts.Current = new ServerAlert("Maintenance at 18:00", AlertSeverity.Info, true, false, true);
    Navigation.NavigateTo("users");

    banner.WaitForAssertion(() => Assert.Single(banner.FindAll("[data-server-alert]")));
  }

  [Fact]
  public void AnAlertThatComesBack_StaysClosedForTheRestOfTheVisit()
  {
    Alerts.Current = new ServerAlert("Maintenance at 18:00", AlertSeverity.Info, IsDismissible: true, ComesBack: true, IsEnabled: true);
    var banner = Render<ServerAlertBanner>();
    banner.WaitForAssertion(() => Assert.Single(banner.FindAll("[data-server-alert]")));

    banner.Find("[data-server-alert] button").Click();
    Navigation.NavigateTo("users");
    Navigation.NavigateTo("permissions");

    Assert.Empty(banner.FindAll("[data-server-alert]"));
    Assert.DoesNotContain(JSInterop.Invocations, x => x.Identifier == "localStorage.setItem");
  }

  [Fact]
  public void AClosedAlert_StaysClosed_ButOneThatComesBackOrChanged_ShowsAgain()
  {
    JSInterop.Setup<string?>("localStorage.getItem", _ => true).SetResult("Warning:Maintenance at 18:00");

    Alerts.Current = new ServerAlert("Maintenance at 18:00", AlertSeverity.Warning, true, ComesBack: false, IsEnabled: true);
    var closed = Render<ServerAlertBanner>();
    Alerts.Current = new ServerAlert("Maintenance at 18:00", AlertSeverity.Warning, true, ComesBack: true, IsEnabled: true);
    var comesBack = Render<ServerAlertBanner>();
    Alerts.Current = new ServerAlert("Maintenance moved to 19:00", AlertSeverity.Warning, true, ComesBack: false, IsEnabled: true);
    var changed = Render<ServerAlertBanner>();
    Alerts.Current = new ServerAlert("Read only", AlertSeverity.Error, IsDismissible: false, ComesBack: false, IsEnabled: true);
    var fixedAlert = Render<ServerAlertBanner>();
    Alerts.Current = new ServerAlert("Hidden", AlertSeverity.Info, true, false, IsEnabled: false);
    var off = Render<ServerAlertBanner>();

    Assert.Empty(closed.FindAll("[data-server-alert]"));
    comesBack.WaitForAssertion(() => Assert.Single(comesBack.FindAll("[data-server-alert]")));
    changed.WaitForAssertion(() => Assert.Single(changed.FindAll("[data-server-alert]")));
    fixedAlert.WaitForAssertion(() => Assert.Single(fixedAlert.FindAll("[data-server-alert]")));
    Assert.Empty(fixedAlert.FindAll("[data-server-alert] button"));
    Assert.Empty(off.FindAll("[data-server-alert]"));
  }

  [Fact]
  public void TheServerLog_ReadsTheServerFirst_ThenAnyTenantPicked()
  {
    var branch = new TenantSummary(Guid.NewGuid(), "Branch", Created, 0, 0);
    Api.RespondWith(Me(PermissionNames.ServerAuthorizationLogsRead, PermissionNames.ServerTenantsRead));
    Api.RespondWith(Tenants(branch));
    Api.RespondWith(new AuthorizationChangePage());
    var page = RenderPage<Pages.ServerAuthorizationLogs>();
    page.WaitForAssertion(() => Assert.Equal(["The server", "Head office", "Branch"], page.FindAll("select")[0].QuerySelectorAll("option").Select(x => x.TextContent)));
    page.WaitForAssertion(() => Assert.Contains(Api.Requests, x => x.StartsWith($"GET {Routes.ServerAuthorizationLogs}?", StringComparison.Ordinal)));

    Api.RespondWith(new AuthorizationChangePage());
    page.FindAll("select")[0].Change(branch.Id.ToString());

    page.WaitForAssertion(() => Assert.Contains(Api.Requests, x => x.Contains($"tenantId={branch.Id}", StringComparison.Ordinal)));
  }

  private IRenderedComponent<CascadingAuthenticationState> RenderPage<TPage>()
    where TPage : Microsoft.AspNetCore.Components.IComponent =>
    Render<CascadingAuthenticationState>(x => x.AddChildContent<TPage>());

  private static AngleSharp.Dom.IElement Button(IRenderedComponent<CascadingAuthenticationState> page, string text) =>
    page.FindAll("button").First(x => x.TextContent.Trim() == text);
}
