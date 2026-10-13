using System.Net;
using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>A service account made in the browser, granted one permission, used by a script, then its key revoked.</summary>
public class ServiceAccountTests
{
  private const string Password = "Choose-a-Passw0rd";

  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task AnIntegrationsKey_ReachesWhatTheAccountWasGranted_UntilItIsRevoked()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);

    await app.ClickLinkAsync("Service accounts");
    await Expect(app.Heading("Service accounts")).ToBeVisibleAsync();
    await app.ClickAsync("New account");
    await app.FillAsync("Name", "GLPI");
    await app.FillAsync("Description", "Pulls the inventory");
    await Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Create" }).ClickAsync();
    var key = (await app.Page.Locator("[data-secret]").InnerTextAsync()).Trim();
    await app.ScreenshotAsync("key-shown-once");

    // A new account has no permissions; Done leads to them.
    await app.ClickAsync("Done");
    await Expect(app.Heading("Permissions")).ToBeVisibleAsync();
    await app.ClickAsync("Add permission");
    await Dialog(app).GetByLabel("Permission", new() { Exact = true }).SelectOptionAsync("tenant.users.read");
    await Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
    await Expect(app.Page.Locator("tr[data-assignment='tenant.users.read']")).ToBeVisibleAsync();

    using var script = new HttpClient { BaseAddress = app.Server.BaseAddress };
    script.DefaultRequestHeaders.Add("x-api-key", key);
    Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("api/v1/users", Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await script.GetAsync("api/v1/permission-assignments", Cancel)).StatusCode);

    await app.ClickLinkAsync("Service accounts");
    var account = app.Page.Locator("tr[data-account='GLPI']");
    await Expect(account).ToContainTextAsync("1 active of 1");
    await account.GetByRole(AriaRole.Button, new() { Name = "1 active of 1" }).ClickAsync();
    await app.Page.Locator("tr[data-credential='Primary']").GetByRole(AriaRole.Button, new() { Name = "Revoke" }).ClickAsync();
    await Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Revoke" }).ClickAsync();
    await Expect(app.Page.Locator("tr[data-credential='Primary']")).ToContainTextAsync("Revoked");
    await app.ScreenshotAsync("key-revoked");

    Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync("api/v1/users", Cancel)).StatusCode);
  }

  private static ILocator Dialog(UiApp app) => app.Page.Locator(".sd-dialog");
}
