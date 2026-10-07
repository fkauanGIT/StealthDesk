using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>Getting in: the first registration, signing out and back in, and pages that need a session.</summary>
public class SignInTests
{
  private const string Password = "Choose-a-Passw0rd";

  [Fact]
  public async Task FirstUser_Registers_SignsOut_AndComesBackWhereTheyWereGoing()
  {
    await using var app = await UiApp.StartAsync();

    await app.RegisterAsync("admin@example.com", Password);
    await Expect(app.Page.Locator(".sd-user-name")).ToHaveTextAsync("admin@example.com");
    await Expect(app.Page.Locator(".sd-user-role")).ToHaveTextAsync("Server administrator");
    await app.SignOutAsync();

    // A page that needs a session sends the visitor to sign in, remembering where they were going.
    await app.GoAsync("account/manage/password");
    await Expect(app.Heading("Sign in to StealthDesk")).ToBeVisibleAsync();
    Assert.Equal("/account/sign-in?returnUrl=%2Faccount%2Fmanage%2Fpassword", app.Location);

    await app.SignInAsync("admin@example.com", "Wrong-Passw0rd");
    await Expect(app.Alert).ToContainTextAsync("Email or password is wrong.");

    await app.SignInAsync("admin@example.com", Password);
    await Expect(app.Page.GetByRole(AriaRole.Heading, new() { Name = "Change password" })).ToBeVisibleAsync();
    Assert.Equal("/account/manage/password", app.Location);
  }

  [Fact]
  public async Task SecondVisitor_CannotRegisterOnceTheServerHasItsAdministrator()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);
    await app.SignOutAsync();

    await Expect(app.Page.GetByRole(AriaRole.Link, new() { Name = "Create an account" })).ToHaveCountAsync(0);
    await app.GoAsync("account/register");

    await Expect(app.Alert).ToContainTextAsync("Registration is closed on this server.");
  }
}
