using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>Devices grouped by department: a group filled, the list filtered by it, and a device moved to another.</summary>
public class DeviceGroupTests
{
  private const string Password = "Choose-a-Passw0rd";

  [Fact]
  public async Task ADevice_IsGrouped_Found_AndMovedToAnotherGroup()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);
    await using var alpha = await UiAgent.StartAsync(app.Server, "PC-ALPHA");
    await using var beta = await UiAgent.StartAsync(app.Server, "PC-BETA");

    await app.OpenAsync("Devices", "Groups");
    await Expect(app.Heading("Device groups")).ToBeVisibleAsync();
    foreach (var name in new[] { "Sales", "HR" })
    {
      await app.OpenAsync("Devices", "Groups");
      await app.ClickAsync("New group");
      await app.FillAsync("Name", name);
      await Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Create" }).ClickAsync();
      await Expect(app.Heading(name)).ToBeVisibleAsync();
    }

    await app.OpenAsync("Devices", "Groups");
    await app.ClickLinkAsync("Sales");
    await app.ClickAsync("Add devices");
    await Dialog(app).GetByLabel("PC-ALPHA").CheckAsync();
    await Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
    await Expect(app.Page.Locator("tr[data-device='PC-ALPHA']")).ToBeVisibleAsync();
    await app.ScreenshotAsync("group");

    await app.OpenAsync("Devices", "Devices");
    await app.Page.GetByLabel("Group", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Sales" });
    await Expect(app.Page.Locator("tbody tr")).ToHaveCountAsync(1);
    await Expect(app.Page.Locator("tbody tr")).ToContainTextAsync("PC-ALPHA");
    await app.ScreenshotAsync("filtered");

    await app.OpenAsync("Devices", "Groups");
    await app.ClickLinkAsync("Sales");
    await app.Page.Locator("tr[data-device='PC-ALPHA']").GetByRole(AriaRole.Button, new() { Name = "Move", Exact = true }).ClickAsync();
    await Dialog(app).GetByLabel("Move to").SelectOptionAsync(new SelectOptionValue { Label = "HR" });
    await Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Move", Exact = true }).ClickAsync();
    await Expect(app.Page.GetByText("No devices yet")).ToBeVisibleAsync();

    await app.GoAsync($"devices/{alpha.DeviceId}");
    await Expect(app.Page.Locator("[data-groups]")).ToHaveTextAsync("HR");

    // From the device's own page: back to Sales, also in HR.
    await app.ClickAsync("Change groups");
    await Dialog(app).GetByLabel("Sales").CheckAsync();
    await Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
    await Expect(app.Page.Locator("[data-groups] a")).ToHaveTextAsync(["HR", "Sales"]);
    await app.ScreenshotAsync("device-groups");
  }

  private static ILocator Dialog(UiApp app) => app.Page.Locator(".sd-dialog");
}
