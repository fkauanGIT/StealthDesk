using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>The users page: a user created through the API appears, and an administrator deletes them.</summary>
public class UsersTests
{
  private const string Password = "Choose-a-Passw0rd";

  [Fact]
  public async Task Administrator_SeesTheTenantsUsers_AndDeletesOne()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);

    // Scripts create users through the API; the browser's session is enough to call it.
    var created = await app.Context.APIRequest.PostAsync("api/v1/users", new APIRequestContextOptions
    {
      DataObject = new { userName = "tech@example.com", password = Password, presets = new[] { "Device Superuser" } },
    });
    Assert.Equal(201, created.Status);

    await app.ClickLinkAsync("Users");
    await Expect(app.Heading("Users")).ToBeVisibleAsync();
    await Expect(app.Page.Locator("tbody tr")).ToHaveCountAsync(2);
    await Expect(Row(app, "admin@example.com").GetByRole(AriaRole.Button, new() { Name = "Delete" })).ToBeDisabledAsync();
    await app.ScreenshotAsync("users");

    await Row(app, "tech@example.com").GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();
    await Expect(app.Page.Locator(".sd-dialog")).ToContainTextAsync("tech@example.com will no longer be able to sign in");
    await app.Page.Locator(".sd-dialog").GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();

    await Expect(app.Page.Locator("tbody tr")).ToHaveCountAsync(1);
    await Expect(Row(app, "tech@example.com")).ToHaveCountAsync(0);
    await app.ScreenshotAsync("deleted");

    var signIn = await app.Context.APIRequest.PostAsync("api/auth/login?useCookies=true", new APIRequestContextOptions
    {
      DataObject = new { email = "tech@example.com", password = Password },
    });
    Assert.Equal(401, signIn.Status);
  }

  private static ILocator Row(UiApp app, string email) => app.Page.Locator($"tr[data-user='{email}']");
}
