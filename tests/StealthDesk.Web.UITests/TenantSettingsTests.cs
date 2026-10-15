using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>A tenant naming an agent instance in its settings, and the install command taking it.</summary>
public class TenantSettingsTests
{
  private const string Password = "Choose-a-Passw0rd";

  [Fact]
  public async Task AnInstanceNamedInTheSettings_GoesIntoTheInstallCommand()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);

    await app.OpenAsync("Settings");
    await Expect(app.Heading("Tenant settings")).ToBeVisibleAsync();
    await Expect(app.Page.Locator("[data-preview]")).ToHaveTextAsync("Install commands won't name an instance.");
    await app.Page.GetByRole(AriaRole.Group, new() { Name = "Name an instance in install commands" })
      .GetByRole(AriaRole.Button, new() { Name = "On", Exact = true }).ClickAsync();
    await app.FillAsync("Instance name", "office");
    await app.ClickAsync("Save");
    await Expect(app.Page.GetByText("Settings saved.")).ToBeVisibleAsync();
    await app.ScreenshotAsync("saved");

    // Saved for the tenant: still there after reloading.
    await app.Page.ReloadAsync();
    await Expect(app.Page.Locator("[data-preview]")).ToHaveTextAsync("Install commands will add --instance office.");

    await app.OpenAsync("Devices");
    await app.ClickLinkAsync("Add device");
    await app.ClickAsync("Create key");
    await Expect(app.Page.Locator("[data-command]")).ToContainTextAsync("--instance office");
  }
}
