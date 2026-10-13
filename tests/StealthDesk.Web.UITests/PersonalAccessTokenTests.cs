using System.Net;
using System.Net.Http.Json;
using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>An access token made in the browser, granted one permission, used by a script, then deleted.</summary>
public class PersonalAccessTokenTests
{
  private const string Password = "Choose-a-Passw0rd";

  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task AScriptsToken_ReachesOnlyWhatItWasGranted_UntilItIsDeleted()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);

    await app.ClickLinkAsync("Access tokens");
    await Expect(app.Heading("Access tokens")).ToBeVisibleAsync();
    await app.FillAsync("Token name", "Inventory script");
    await app.ClickAsync("Create token");
    var secret = (await app.Page.Locator("[data-secret]").InnerTextAsync()).Trim();
    await app.ScreenshotAsync("secret-shown-once");

    // Restricted tokens start with nothing; Done leads to its permissions.
    await app.ClickAsync("Done");
    await Expect(app.Heading("Permissions")).ToBeVisibleAsync();
    await Expect(app.Page.GetByText("No permissions")).ToBeVisibleAsync();
    await app.ClickAsync("Add permission");
    await Dialog(app).GetByLabel("Permission", new() { Exact = true }).SelectOptionAsync("tenant.users.read");
    await Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
    await Expect(app.Page.Locator("tr[data-assignment='tenant.users.read']")).ToBeVisibleAsync();
    await app.ScreenshotAsync("token-granted");

    // A script with no session: only the header.
    using var script = new HttpClient { BaseAddress = app.Server.BaseAddress };
    script.DefaultRequestHeaders.Add("x-personal-token", secret);
    var me = await script.GetFromJsonAsync<Me>("api/auth/me", Cancel);
    var users = await script.GetAsync($"api/v1/users?tenantId={me!.TenantId}", Cancel);
    var assignments = await script.GetAsync($"api/v1/permission-assignments?tenantId={me.TenantId}", Cancel);
    Assert.Equal("admin@example.com", me.Email);
    Assert.Equal(HttpStatusCode.OK, users.StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, assignments.StatusCode);

    await app.ClickLinkAsync("Access tokens");
    var row = app.Page.Locator("tr[data-token='Inventory script']");
    await Expect(row).ToContainTextAsync("1");
    await Expect(row.Locator("[data-cell=expires]")).Not.ToContainTextAsync("Never");
    await row.GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();
    await Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();
    await Expect(app.Page.GetByText("No access tokens")).ToBeVisibleAsync();

    Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync($"api/v1/users?tenantId={me.TenantId}", Cancel)).StatusCode);
  }

  private static ILocator Dialog(UiApp app) => app.Page.Locator(".sd-dialog");

  private sealed record Me(string Email, Guid TenantId);
}
