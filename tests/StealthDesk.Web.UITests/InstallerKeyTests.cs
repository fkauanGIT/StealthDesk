using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>
/// A key made on the Deploy page installs an agent from the command it shows, its install is listed, and revoking it
/// stops new installs while the device keeps reporting.
/// </summary>
public class InstallerKeyTests
{
  private const string Password = "Choose-a-Passw0rd";

  [Fact]
  public async Task AKeyFromTheDeployPage_InstallsAnAgent_UntilItIsRevoked()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);

    await app.ClickLinkAsync("Deploy");
    await Expect(app.Heading("Deploy an agent")).ToBeVisibleAsync();
    await app.FillAsync("Name (optional)", "Front desk");
    await app.FillAsync("Uses", "2");
    await app.ScreenshotAsync("deploy-new-key");
    await app.ClickAsync("Create key");
    var command = (await app.Page.Locator("[data-command]").InnerTextAsync()).Trim();
    await app.ScreenshotAsync("deploy-command");

    var key = command.Split("--installer-key ")[1].Trim();
    Assert.StartsWith($@".\StealthDesk.Agent.exe run --server {app.Server.BaseAddress.GetLeftPart(UriPartial.Authority)}", command);
    await using var agent = await UiAgent.StartAsync(app.Server, "PC-FRONT", key);

    await app.ClickLinkAsync("Installer keys");
    var row = app.Page.Locator("tr[data-key='Front desk']");
    await Expect(row).ToContainTextAsync("1 of 2");
    await row.GetByRole(AriaRole.Button, new() { Name = "Usages" }).ClickAsync();
    await Expect(app.Page.Locator("[data-usages]")).ToContainTextAsync(agent.DeviceId.ToString());
    await app.ScreenshotAsync("key-usages");
    await Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();

    await row.GetByRole(AriaRole.Button, new() { Name = "Revoke" }).ClickAsync();
    await Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Revoke" }).ClickAsync();
    await Expect(app.Page.GetByText("No installer keys")).ToBeVisibleAsync();

    var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => UiAgent.StartAsync(app.Server, "PC-LATE", key));
    Assert.Contains("Invalid installer key.", refused.Message);
    await app.ClickLinkAsync("Devices");
    await Expect(app.Page.Locator("tbody tr").Filter(new() { HasText = "PC-FRONT" }).Locator(".device-status")).ToHaveTextAsync("Online");
    await Expect(app.Page.Locator("tbody tr").Filter(new() { HasText = "PC-LATE" })).ToHaveCountAsync(0);
  }

  private static ILocator Dialog(UiApp app) => app.Page.Locator(".sd-dialog");
}
