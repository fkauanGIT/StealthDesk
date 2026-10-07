using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>
/// Passkeys through the browser's WebAuthn and the site's passkeys.js, with Chromium's virtual authenticator standing
/// in for Windows Hello: a platform authenticator whose fingerprint or PIN is always accepted.
/// </summary>
public class PasskeyTests
{
  private const string Password = "Choose-a-Passw0rd";

  [Fact]
  public async Task Passkey_Added_SignsIn_IsRenamed_AndOnceRemovedIsRefused()
  {
    await using var app = await UiApp.StartAsync();
    var devtools = await app.Context.NewCDPSessionAsync(app.Page);
    await devtools.SendAsync("WebAuthn.enable");
    var authenticator = (await devtools.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
    {
      ["options"] = new Dictionary<string, object>
      {
        ["protocol"] = "ctap2",
        ["transport"] = "internal",
        ["hasResidentKey"] = true,
        ["hasUserVerification"] = true,
        ["isUserVerified"] = true,
        ["automaticPresenceSimulation"] = true,
      },
    }))!.Value.GetProperty("authenticatorId").GetString()!;

    // Without simulated touches, the email field's autofill waits instead of signing in by itself.
    Task Touches(bool on) => devtools.SendAsync("WebAuthn.setAutomaticPresenceSimulation",
      new Dictionary<string, object> { ["authenticatorId"] = authenticator, ["enabled"] = on });

    await app.RegisterAsync("admin@example.com", Password);
    await app.GoAsync("account/manage/passkeys");
    await Expect(app.Page.Locator(".sd-empty-title")).ToHaveTextAsync("No passkeys yet");
    await app.ClickAsync("Add a passkey");
    await Expect(app.Page.GetByRole(AriaRole.Heading, new() { Name = "Name your new passkey" })).ToBeVisibleAsync();
    await app.FillAsync("Name", "Virtual authenticator");
    await app.ClickAsync("Save");
    await Expect(app.Page.Locator(".sd-passkey-name")).ToHaveTextAsync("Virtual authenticator");

    await Touches(false);
    await app.SignOutAsync();
    await Touches(true);
    await app.ClickAsync("Sign in with a passkey");
    await Expect(app.Heading("Devices")).ToBeVisibleAsync();

    await app.GoAsync("account/manage/passkeys");
    await app.ClickLinkAsync("Rename");
    await app.FillAsync("Name", "Work laptop");
    await app.ClickAsync("Save");
    await Expect(app.Page.Locator(".sd-passkey-name")).ToHaveTextAsync("Work laptop");

    await app.ClickAsync("Remove");
    await Expect(app.Page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("\"Work laptop\" will no longer sign you in.");
    await app.Page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
    await Expect(app.Page.Locator(".sd-empty-title")).ToHaveTextAsync("No passkeys yet");

    // The device still has the key, but the server no longer knows it.
    await Touches(false);
    await app.SignOutAsync();
    await Touches(true);
    await app.ClickAsync("Sign in with a passkey");
    await Expect(app.Alert).ToContainTextAsync("This passkey isn't registered here");
    Assert.StartsWith("/account/sign-in", app.Location, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Passkey_OfferedInTheEmailField_SignsIn()
  {
    await using var app = await UiApp.StartAsync();
    var devtools = await app.Context.NewCDPSessionAsync(app.Page);
    await devtools.SendAsync("WebAuthn.enable");
    await devtools.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
    {
      ["options"] = new Dictionary<string, object>
      {
        ["protocol"] = "ctap2",
        ["transport"] = "internal",
        ["hasResidentKey"] = true,
        ["hasUserVerification"] = true,
        ["isUserVerified"] = true,
        ["automaticPresenceSimulation"] = true,
      },
    });
    await app.RegisterAsync("admin@example.com", Password);
    await app.GoAsync("account/manage/passkeys");
    await app.ClickAsync("Add a passkey");
    await app.FillAsync("Name", "Virtual authenticator");
    await app.ClickAsync("Save");
    await Expect(app.Page.Locator(".sd-passkey")).ToHaveCountAsync(1);

    // The virtual authenticator picks the offered passkey at once, as a user would from the field's list.
    await app.Page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();

    await Expect(app.Heading("Devices")).ToBeVisibleAsync();
    Assert.Equal("/", app.Location);
  }
}
