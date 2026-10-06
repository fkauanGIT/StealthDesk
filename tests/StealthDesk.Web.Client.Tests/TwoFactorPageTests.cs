using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.Pages.Account;
using StealthDesk.Web.Client.Pages.Account.Manage;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The second sign-in step and the two-factor settings pages, against a fake server.</summary>
public class TwoFactorPageTests : AccountTestContext
{
  private static readonly string[] TenCodes = [.. Enumerable.Range(0, 10).Select(i => $"ABCD{i}-EFGH{i}")];

  public TwoFactorPageTests()
  {
    Services.AddSingleton<SessionGuard>();
  }

  // ---------- Signing in ----------

  [Fact]
  public void SignIn_WhenTheAccountNeedsACode_GoesToTheCodeStep()
  {
    Api.RespondWith(new AccountSettings());
    Api.Respond(HttpStatusCode.Unauthorized, new { detail = "RequiresTwoFactor" });
    Navigation.NavigateTo("account/sign-in?returnUrl=%2Fdevices%2F42");
    var page = Render<SignIn>();

    Fill(page, "Email", "ana@example.com");
    page.Find("#password").Change("Correct-horse-9");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("account/sign-in/two-factor?rememberMe=true&returnUrl=%2Fdevices%2F42", Location));
  }

  [Fact]
  public void Code_SignsInAndGoesWhereTheUserWasHeading()
  {
    Navigation.NavigateTo("account/sign-in/two-factor?rememberMe=true&returnUrl=%2Fdevices%2F42");
    var page = Render<SignInTwoFactor>();
    Api.Respond(HttpStatusCode.OK);
    Api.RespondWith(new CurrentUser { Email = "ana@example.com" });

    Fill(page, "Authenticator code", "123 456");
    page.Find("input[type=checkbox]").Change(true);
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("devices/42", Location));
    Assert.Equal("POST /api/auth/two-factor", Api.Requests[0]);
    Assert.Contains("\"code\":\"123 456\"", Api.Bodies[0]);
    Assert.Contains("\"rememberMe\":true", Api.Bodies[0]);
    Assert.Contains("\"rememberBrowser\":true", Api.Bodies[0]);
  }

  [Fact]
  public void Code_Wrong_SaysSoAndOffersToSignInAgain()
  {
    Navigation.NavigateTo("account/sign-in/two-factor?returnUrl=%2Fdevices%2F42");
    var page = Render<SignInTwoFactor>();
    Api.Respond(HttpStatusCode.Unauthorized, new { detail = "Failed" });

    Fill(page, "Authenticator code", "000000");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("The code is wrong, or this sign-in has expired.", page.Find(".sd-alert").TextContent));
    Assert.Equal("account/sign-in?returnUrl=%2Fdevices%2F42", page.Find(".sd-alert a").GetAttribute("href"));
    Assert.Equal("account/sign-in/two-factor?returnUrl=%2Fdevices%2F42", Location);
  }

  [Fact]
  public void Code_LockedOut_GoesToTheLockedOutPage()
  {
    var page = Render<SignInTwoFactor>();
    Api.Respond(HttpStatusCode.Unauthorized, new { detail = "LockedOut" });

    Fill(page, "Authenticator code", "000000");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("account/locked-out", Location));
  }

  [Fact]
  public void Code_Empty_IsCaughtWithoutCallingTheServer()
  {
    var page = Render<SignInTwoFactor>();

    page.Find("form").Submit();

    Assert.Equal("Enter the code from your app.", ErrorOf(page, "Authenticator code").TextContent);
    Assert.Empty(Api.Requests);
  }

  [Fact]
  public void Code_ThatLooksLikeARecoveryCode_PointsToTheRecoveryPageWithoutCallingTheServer()
  {
    Navigation.NavigateTo("account/sign-in/two-factor?returnUrl=%2Fdevices%2F42");
    var page = Render<SignInTwoFactor>();

    Fill(page, "Authenticator code", " HD4JW-K4JN7 ");
    page.Find("form").Submit();

    Assert.Contains("That looks like a recovery code.", page.Find(".sd-alert").TextContent);
    Assert.Equal("account/sign-in/recovery-code?returnUrl=%2Fdevices%2F42", page.Find(".sd-alert a").GetAttribute("href"));
    Assert.Empty(Api.Requests);
  }

  [Fact]
  public void Code_LinksToTheRecoveryCodeKeepingTheReturnAddress()
  {
    Navigation.NavigateTo("account/sign-in/two-factor?returnUrl=%2Fdevices%2F42");

    var page = Render<SignInTwoFactor>();

    Assert.Equal("account/sign-in/recovery-code?returnUrl=%2Fdevices%2F42", page.Find(".sd-auth-footer a").GetAttribute("href"));
  }

  [Fact]
  public void RecoveryCode_SignsIn()
  {
    Navigation.NavigateTo("account/sign-in/recovery-code?returnUrl=%2Fdevices%2F42");
    var page = Render<SignInRecoveryCode>();
    Api.Respond(HttpStatusCode.OK);
    Api.RespondWith(new CurrentUser { Email = "ana@example.com" });

    Fill(page, "Recovery code", " ABCD1-EFGH1 ");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("devices/42", Location));
    Assert.Equal("POST /api/auth/recovery-code", Api.Requests[0]);
    Assert.Contains("\"recoveryCode\":\"ABCD1-EFGH1\"", Api.Bodies[0]);
  }

  [Fact]
  public void RecoveryCode_UsedOrWrong_SaysSo()
  {
    var page = Render<SignInRecoveryCode>();
    Api.Respond(HttpStatusCode.Unauthorized, new { detail = "Failed" });

    Fill(page, "Recovery code", "ABCD1-EFGH1");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("wrong or already used", page.Find(".sd-alert").TextContent));
  }

  // ---------- Settings ----------

  [Fact]
  public void Overview_Off_OffersToAddTheApp()
  {
    Api.RespondWith(new TwoFactorStatus());

    var page = Render<TwoFactor>();

    page.WaitForAssertion(() => Assert.Equal("Off", page.Find(".sd-status").TextContent));
    Assert.Equal("account/manage/two-factor/enable", page.FindAll("a").Single(x => x.TextContent == "Add authenticator app").GetAttribute("href"));
    Assert.DoesNotContain(page.FindAll("a"), x => x.TextContent == "Turn off");
  }

  [Fact]
  public void Overview_On_WarnsWhenFewRecoveryCodesAreLeft()
  {
    Api.RespondWith(new TwoFactorStatus { Enabled = true, HasAuthenticator = true, RecoveryCodesLeft = 1 });

    var page = Render<TwoFactor>();

    page.WaitForAssertion(() => Assert.Equal("On", page.Find(".sd-status").TextContent));
    Assert.Contains("You have 1 recovery code left.", page.Find(".sd-alert--warning").TextContent);
    Assert.Contains(page.FindAll("a"), x => x.TextContent == "Reset authenticator app");
  }

  [Fact]
  public void Overview_NoRecoveryCodesLeft_SaysSoLoudly()
  {
    Api.RespondWith(new TwoFactorStatus { Enabled = true, HasAuthenticator = true, RecoveryCodesLeft = 0 });

    var page = Render<TwoFactor>();

    page.WaitForAssertion(() => Assert.Contains("You have no recovery codes left.", page.Find(".sd-alert--danger").TextContent));
  }

  [Fact]
  public void Overview_ForgetsThisBrowser()
  {
    Api.RespondWith(new TwoFactorStatus { Enabled = true, HasAuthenticator = true, RecoveryCodesLeft = 10, IsBrowserRemembered = true });
    var page = Render<TwoFactor>();
    page.WaitForAssertion(() => page.FindAll("button").Single(x => x.TextContent == "Forget this browser"));
    Api.Respond(HttpStatusCode.NoContent);

    page.FindAll("button").Single(x => x.TextContent == "Forget this browser").Click();

    page.WaitForAssertion(() => Assert.Contains("This browser is forgotten.", page.Find(".sd-alert").TextContent));
    Assert.Contains("POST /api/account/two-factor/forget-browser", Api.Requests);
    Assert.DoesNotContain(page.FindAll("button"), x => x.TextContent == "Forget this browser");
  }

  [Fact]
  public void Overview_AfterTurningOff_SaysWhatHappened()
  {
    Navigation.NavigateTo("account/manage/two-factor?done=disabled");
    Api.RespondWith(new TwoFactorStatus { HasAuthenticator = true });

    var page = Render<TwoFactor>();

    page.WaitForAssertion(() => Assert.Contains("Two-factor authentication is off.", page.Find(".sd-alert").TextContent));
  }

  [Fact]
  public void Enable_ShowsTheKeyAndQrCode_ThenTheRecoveryCodes()
  {
    Api.RespondWith(new AuthenticatorSetup { SharedKey = "abcd efgh ijkl", AuthenticatorUri = "otpauth://totp/x", QrCode = "data:image/png;base64,AAAA" });
    var page = Render<EnableAuthenticator>();
    page.WaitForAssertion(() => Assert.Equal("abcd efgh ijkl", page.Find(".sd-key").TextContent));
    Api.RespondWith(new RecoveryCodeSet { Codes = TenCodes });

    Assert.Equal("data:image/png;base64,AAAA", page.Find("img.sd-qr").GetAttribute("src"));
    Fill(page, "Verification code", "123456");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal(10, page.FindAll(".sd-codes li").Count));
    Assert.Equal("ABCD0-EFGH0", page.Find(".sd-codes li").TextContent);
    Assert.Contains("POST /api/account/two-factor/enable", Api.Requests);
  }

  [Fact]
  public void Enable_KeepingTheOldRecoveryCodes_GoesBackToTheOverview()
  {
    Api.RespondWith(new AuthenticatorSetup { SharedKey = "abcd", QrCode = "data:image/png;base64,AAAA" });
    var page = Render<EnableAuthenticator>();
    page.WaitForAssertion(() => page.Find("form"));
    Api.RespondWith(new RecoveryCodeSet());

    Fill(page, "Verification code", "123456");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("account/manage/two-factor?done=enabled", Location));
  }

  [Fact]
  public void Enable_WrongCode_ShowsUnderTheField()
  {
    Api.RespondWith(new AuthenticatorSetup { SharedKey = "abcd", QrCode = "data:image/png;base64,AAAA" });
    var page = Render<EnableAuthenticator>();
    page.WaitForAssertion(() => page.Find("form"));
    Api.Respond(HttpStatusCode.BadRequest,
      new { errors = new Dictionary<string, string[]> { ["InvalidCode"] = ["The verification code is wrong. Check the app and try again."] } });

    Fill(page, "Verification code", "000000");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("The verification code is wrong. Check the app and try again.", ErrorOf(page, "Verification code").TextContent));
    Assert.Empty(page.FindAll(".sd-codes"));
  }

  [Fact]
  public void Enable_AfterAReset_SaysTheKeyIsNew()
  {
    Navigation.NavigateTo("account/manage/two-factor/enable?reset=true");
    Api.RespondWith(new AuthenticatorSetup { SharedKey = "abcd", QrCode = "data:image/png;base64,AAAA" });

    var page = Render<EnableAuthenticator>();

    page.WaitForAssertion(() => Assert.Contains("Your authenticator key was reset.", page.Find(".sd-alert").TextContent));
  }

  [Fact]
  public void RecoveryCodes_GeneratesAndShowsThem()
  {
    var page = Render<RecoveryCodes>();
    Api.RespondWith(new RecoveryCodeSet { Codes = TenCodes });

    page.FindAll("button").Single(x => x.TextContent == "Generate new codes").Click();

    page.WaitForAssertion(() => Assert.Equal(10, page.FindAll(".sd-codes li").Count));
    Assert.Equal(["POST /api/account/two-factor/recovery-codes"], Api.Requests);
  }

  [Fact]
  public void RecoveryCodes_WithTwoFactorOff_SaysWhy()
  {
    var page = Render<RecoveryCodes>();
    Api.Respond(HttpStatusCode.BadRequest, new { detail = "Turn on two-factor authentication before generating recovery codes." });

    page.FindAll("button").Single(x => x.TextContent == "Generate new codes").Click();

    page.WaitForAssertion(() => Assert.Contains("Turn on two-factor", page.Find(".sd-alert--danger").TextContent));
    Assert.Empty(page.FindAll(".sd-codes"));
  }

  [Fact]
  public void Reset_GoesToSetUpTheAppAgain()
  {
    var page = Render<ResetAuthenticator>();
    Api.Respond(HttpStatusCode.NoContent);

    page.Find("button.sd-btn--danger").Click();

    page.WaitForAssertion(() => Assert.Equal("account/manage/two-factor/enable?reset=true", Location));
    Assert.Equal(["POST /api/account/two-factor/authenticator/reset"], Api.Requests);
  }

  [Fact]
  public void Disable_TurnsItOffAndGoesBack()
  {
    var page = Render<DisableTwoFactor>();
    Api.Respond(HttpStatusCode.NoContent);

    page.Find("button.sd-btn--danger").Click();

    page.WaitForAssertion(() => Assert.Equal("account/manage/two-factor?done=disabled", Location));
    Assert.Equal(["POST /api/account/two-factor/disable"], Api.Requests);
  }
}
