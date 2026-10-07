using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>Two-factor with an authenticator app, its codes computed from the key the page shows.</summary>
public class TwoFactorTests
{
  private const string Password = "Choose-a-Passw0rd";

  [Fact]
  public async Task TwoFactor_SetUp_SignIn_RememberedBrowser_AndRecoveryCode()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);
    await app.GoAsync("account/manage/two-factor");
    await app.ClickLinkAsync("Add authenticator app");

    var key = await app.Page.Locator(".sd-key").TextContentAsync() ?? string.Empty;
    await Expect(app.Page.GetByAltText("QR code for the authenticator app")).ToBeVisibleAsync();
    await app.FillAsync("Verification code", "000000");
    await app.ClickAsync("Verify");
    await Expect(app.ErrorOf("Verification code")).ToContainTextAsync("wrong");
    await app.FillAsync("Verification code", Totp.Code(key));
    await app.ClickAsync("Verify");
    await Expect(app.Page.Locator(".sd-codes li")).ToHaveCountAsync(10);
    var codes = await app.Page.Locator(".sd-codes li").AllTextContentsAsync();
    await app.ScreenshotAsync("recovery-codes");

    // The password alone isn't enough any more.
    await app.SignOutAsync();
    await app.SignInAsync("admin@example.com", Password);
    await Expect(app.Heading("Two-factor authentication")).ToBeVisibleAsync();
    await app.FillAsync("Authenticator code", "000000");
    await app.ClickAsync("Sign in");
    await Expect(app.Alert).ToContainTextAsync("The code is wrong, or this sign-in has expired.");

    // A recovery code typed in the app's field points to the right page instead of failing.
    await app.FillAsync("Authenticator code", codes[0]);
    await app.ClickAsync("Sign in");
    await Expect(app.Alert).ToContainTextAsync("That looks like a recovery code.");

    await app.FillAsync("Authenticator code", Totp.Code(key));
    await app.Page.GetByLabel("Remember this browser").CheckAsync();
    await app.ClickAsync("Sign in");
    await Expect(app.Heading("Devices")).ToBeVisibleAsync();

    // A remembered browser signs in with the password alone.
    await app.SignOutAsync();
    await app.SignInAsync("admin@example.com", Password);
    await Expect(app.Heading("Devices")).ToBeVisibleAsync();

    await app.GoAsync("account/manage/two-factor");
    await app.ClickAsync("Forget this browser");
    await Expect(app.Alert).ToContainTextAsync("This browser is forgotten.");
    await app.SignOutAsync();
    await app.SignInAsync("admin@example.com", Password);
    await app.ClickLinkAsync("Use a recovery code");
    await app.FillAsync("Recovery code", codes[0]);
    await app.ClickAsync("Sign in");
    await Expect(app.Heading("Devices")).ToBeVisibleAsync();

    // Each recovery code works once.
    await app.SignOutAsync();
    await app.SignInAsync("admin@example.com", Password);
    await app.ClickLinkAsync("Use a recovery code");
    await app.FillAsync("Recovery code", codes[0]);
    await app.ClickAsync("Sign in");
    await Expect(app.Alert).ToContainTextAsync("wrong or already used");
  }
}
