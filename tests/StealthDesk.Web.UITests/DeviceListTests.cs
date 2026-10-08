using Npgsql;
using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>The device list with agents connected over the network: live status, and only the devices the user may read.</summary>
public class DeviceListTests
{
  private const string Password = "Choose-a-Passw0rd";

  [Fact]
  public async Task Devices_ShowTheirLiveStatus_AndOnlyThoseTheUserMayRead()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);
    var alpha = await UiAgent.StartAsync(app.Server, "PC-ALPHA");
    await using var beta = await UiAgent.StartAsync(app.Server, "PC-BETA");

    // Devices that joined after the page loaded appear on the next load.
    await app.Page.ReloadAsync();
    await Expect(StatusOf(app, "PC-ALPHA")).ToHaveTextAsync("Online");
    await Expect(StatusOf(app, "PC-BETA")).ToHaveTextAsync("Online");
    await app.ScreenshotAsync("both-online");

    // From then on they are live: the page changes without reloading.
    await alpha.DisposeAsync();
    await Expect(StatusOf(app, "PC-ALPHA")).ToHaveTextAsync("Offline");
    await Expect(StatusOf(app, "PC-BETA")).ToHaveTextAsync("Online");
    await app.ScreenshotAsync("alpha-offline");

    // Narrowed to one device, the administrator sees only that one, and the other's page is not found.
    await ReadOnlyAsync(app.Server, beta.DeviceId);
    await app.Page.ReloadAsync();
    await Expect(StatusOf(app, "PC-BETA")).ToHaveTextAsync("Online");
    await Expect(Row(app, "PC-ALPHA")).ToHaveCountAsync(0);
    await app.ScreenshotAsync("only-beta");

    await app.GoAsync($"devices/{alpha.DeviceId}");
    await Expect(app.Page.GetByText("Device not found")).ToBeVisibleAsync();
  }

  [Fact]
  public async Task WithoutDeviceRead_TheListIsEmpty()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);
    await using var agent = await UiAgent.StartAsync(app.Server, "PC-ALPHA");

    await ExecuteAsync(app.Server, "DELETE FROM permission_assignments WHERE \"Permission\" = 'device.read'");
    await app.Page.ReloadAsync();

    await Expect(app.Page.GetByText("No devices yet")).ToBeVisibleAsync();
    await Expect(Row(app, "PC-ALPHA")).ToHaveCountAsync(0);
    await app.ScreenshotAsync("empty");
  }

  private static ILocator Row(UiApp app, string name) =>
    app.Page.Locator("tbody tr").Filter(new LocatorFilterOptions { HasText = name });

  private static ILocator StatusOf(UiApp app, string name) => Row(app, name).Locator(".device-status");

  // The permission pages come later: until then the test narrows the grant in the database, as they will.
  private static Task ReadOnlyAsync(UiServer server, Guid deviceId) =>
    ExecuteAsync(
      server,
      $"UPDATE permission_assignments SET \"ScopeKind\" = 'Device', \"ScopeId\" = '{deviceId}' WHERE \"Permission\" = 'device.read'");

  private static async Task ExecuteAsync(UiServer server, string sql)
  {
    await using var connection = new NpgsqlConnection(server.Database);
    await connection.OpenAsync(TestContext.Current.CancellationToken);
    await using var command = new NpgsqlCommand(sql, connection);
    Assert.True(await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken) > 0, "The statement changed no rows.");
  }
}
