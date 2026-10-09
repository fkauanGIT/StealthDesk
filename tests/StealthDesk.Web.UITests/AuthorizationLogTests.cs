using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>The authorization log in the browser: who sees it, what an entry shows, and filtering.</summary>
public class AuthorizationLogTests
{
  private const string Password = "Choose-a-Passw0rd";

  [Fact]
  public async Task Administrator_SeesTheGrantRegistrationMade_WithItsBeforeAndAfter()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);

    await app.ClickLinkAsync("Authorization log");
    await Expect(app.Heading("Authorization log")).ToBeVisibleAsync();
    var row = app.Page.Locator(".sd-log-table tbody tr").First;
    await Expect(row).ToContainTextAsync("permission-assignments-seeded");
    await Expect(row).ToContainTextAsync("system");
    await app.ScreenshotAsync("log");

    await app.Page.GetByRole(AriaRole.Button, new() { Name = "Show before and after" }).ClickAsync();
    await Expect(app.Page.Locator(".sd-log-json").Nth(0)).ToHaveTextAsync("(none)");
    await Expect(app.Page.Locator(".sd-log-json").Nth(1)).ToContainTextAsync("\"presets\"");
    await app.ScreenshotAsync("expanded");

    // A filter that matches nothing empties the table; clearing it brings the entry back.
    await app.Page.GetByLabel("Actor", new() { Exact = true }).SelectOptionAsync("user");
    await app.ClickAsync("Apply filters");
    await Expect(app.Page.GetByText("No changes recorded")).ToBeVisibleAsync();
    await app.Page.GetByLabel("Actor", new() { Exact = true }).SelectOptionAsync("");
    await app.ClickAsync("Apply filters");
    await Expect(app.Page.Locator(".sd-log-table tbody tr")).ToHaveCountAsync(1);

    await app.ClickLinkAsync("Server log");
    await Expect(app.Heading("Server authorization log")).ToBeVisibleAsync();
  }

  [Fact]
  public async Task TenantAdministrator_ReadsTheirLog_ButNotTheServers()
  {
    await using var app = await UiApp.StartAsync(("Accounts:EnablePublicRegistration", "true"));
    await app.RegisterAsync("admin@example.com", Password);
    await app.SignOutAsync();

    // Email sending is off in these tests, so the second account can sign in right away.
    await app.RegisterAsync("owner@example.com", Password);

    await Expect(app.Page.GetByRole(AriaRole.Link, new() { Name = "Server log" })).ToHaveCountAsync(0);
    await app.ClickLinkAsync("Authorization log");
    await Expect(app.Page.Locator(".sd-log-table tbody tr")).ToHaveCountAsync(1);

    await app.GoAsync("server/authorization-logs");
    await Expect(app.Heading("Access denied")).ToBeVisibleAsync();
    await app.ScreenshotAsync("denied");
  }
}
