using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>The server's administrator: a tenant created, renamed and deleted; an alert for everyone; a test email.</summary>
public class ServerAdministrationTests
{
  private const string Password = "Choose-a-Passw0rd";

  [Fact]
  public async Task TheAdministrator_ManagesTenants_AndTellsEveryoneAboutMaintenance()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);

    await app.OpenAsync("Server", "Tenants");
    await Expect(app.Heading("Tenants")).ToBeVisibleAsync();
    await app.FillAsync("New tenant", "Branch office");
    await app.ClickAsync("Create tenant");
    var branch = app.Page.Locator("tr[data-tenant='Branch office']");
    await Expect(branch).ToBeVisibleAsync();
    await branch.GetByRole(AriaRole.Button, new() { Name = "Rename" }).ClickAsync();
    await app.FillAsync("Name", "Branch");
    await Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Rename" }).ClickAsync();
    branch = app.Page.Locator("tr[data-tenant='Branch']");
    await Expect(branch).ToBeVisibleAsync();
    await app.ScreenshotAsync("tenants");

    await branch.GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();
    await Expect(Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Delete tenant" })).ToBeDisabledAsync();
    await app.FillAsync("Type the tenant's name to confirm", "Branch");
    await Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Delete tenant" }).ClickAsync();
    await Expect(app.Page.Locator("tbody tr")).ToHaveCountAsync(1);

    await app.OpenAsync("Server", "Server settings");
    await app.FillAsync("Message", "Maintenance tonight at 18:00");
    await app.Page.GetByRole(AriaRole.Group, new() { Name = "Severity" }).GetByRole(AriaRole.Button, new() { Name = "Warning" }).ClickAsync();
    await app.ClickAsync("Save alert");
    await Expect(app.Page.GetByText("The alert is shown to everyone.")).ToBeVisibleAsync();
    await app.ClickAsync("Send test email");
    await Expect(app.Page.GetByText("went to the server's log")).ToBeVisibleAsync();
    await app.ScreenshotAsync("server-settings");

    // Every page shows it, until closed: then it stays closed in this browser.
    await app.OpenAsync("Devices");
    var alert = app.Page.Locator("[data-server-alert]");
    await Expect(alert).ToContainTextAsync("Maintenance tonight at 18:00");
    await app.ScreenshotAsync("alert");
    await alert.GetByRole(AriaRole.Button, new() { Name = "Close the alert" }).ClickAsync();
    await Expect(alert).ToHaveCountAsync(0);
    await app.Page.ReloadAsync();
    await Expect(app.Heading("Devices")).ToBeVisibleAsync();
    await Expect(alert).ToHaveCountAsync(0);
  }

  private static ILocator Dialog(UiApp app) => app.Page.Locator(".sd-dialog");
}
