using System.Net;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Web.Client.Pages.Account;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The account pages, each in its success and error states, against a fake server.</summary>
public class AccountPageTests : AccountTestContext
{
  private static readonly object WeakPassword = new
  {
    errors = new Dictionary<string, string[]>
    {
      ["PasswordTooShort"] = ["Passwords must be at least 8 characters."],
      ["PasswordRequiresDigit"] = ["Passwords must have at least one digit ('0'-'9')."],
    },
  };

  // ---------- Sign in ----------

  [Fact]
  public void SignIn_GoesBackWhereTheUserWasHeading()
  {
    Api.RespondWith(new AccountSettings { RegistrationOpen = false });
    Api.Respond(HttpStatusCode.OK);
    Navigation.NavigateTo("account/sign-in?returnUrl=%2Fdevices%2F42");
    var page = Render<SignIn>();

    Fill(page, "Email", "ana@example.com");
    page.Find("#password").Change("Correct-horse-9");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("devices/42", Location));
    Assert.Contains("POST /api/auth/login?useCookies=true", Api.Requests);
  }

  [Fact]
  public void SignIn_NeverFollowsAReturnAddressOnAnotherSite()
  {
    Api.RespondWith(new AccountSettings());
    Api.Respond(HttpStatusCode.OK);
    Navigation.NavigateTo("account/sign-in?returnUrl=%2F%2Fevil.example");
    var page = Render<SignIn>();

    Fill(page, "Email", "ana@example.com");
    page.Find("#password").Change("Correct-horse-9");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal(string.Empty, Location));
  }

  [Fact]
  public void SignIn_WithoutRememberMe_UsesASessionCookie()
  {
    Api.RespondWith(new AccountSettings());
    Api.Respond(HttpStatusCode.OK);
    var page = Render<SignIn>();

    Fill(page, "Email", "ana@example.com");
    page.Find("#password").Change("Correct-horse-9");
    page.Find("input[type=checkbox]").Change(false);
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("POST /api/auth/login?useSessionCookies=true", Api.Requests));
  }

  [Fact]
  public void SignIn_WrongPassword_SaysSoAndStays()
  {
    Api.RespondWith(new AccountSettings());
    Api.Respond(HttpStatusCode.Unauthorized, new { detail = "Failed" });
    Navigation.NavigateTo("account/sign-in");
    var page = Render<SignIn>();

    Fill(page, "Email", "ana@example.com");
    page.Find("#password").Change("Wrong-horse-9");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("Email or password is wrong.", page.Find(".sd-alert").TextContent));
    Assert.Equal("account/sign-in", Location);
  }

  [Fact]
  public void SignIn_LockedOut_GoesToTheLockedOutPage()
  {
    Api.RespondWith(new AccountSettings());
    Api.Respond(HttpStatusCode.Unauthorized, new { detail = "LockedOut" });
    var page = Render<SignIn>();

    Fill(page, "Email", "ana@example.com");
    page.Find("#password").Change("Wrong-horse-9");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("account/locked-out", Location));
  }

  [Fact]
  public void SignIn_Unconfirmed_OffersTheLinkAgain()
  {
    Api.RespondWith(new AccountSettings());
    Api.Respond(HttpStatusCode.Unauthorized, new { detail = "NotAllowed" });
    var page = Render<SignIn>();

    Fill(page, "Email", "ana@example.com");
    page.Find("#password").Change("Correct-horse-9");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("Confirm your email", page.Find(".sd-alert").TextContent));
    Assert.Equal("account/resend-confirmation?email=ana%40example.com", page.Find(".sd-alert a").GetAttribute("href"));
  }

  [Fact]
  public void SignIn_EmptyForm_AsksForTheFieldsWithoutCallingTheServer()
  {
    Api.RespondWith(new AccountSettings());
    var page = Render<SignIn>();
    page.WaitForAssertion(() => Assert.Single(Api.Requests));

    page.Find("form").Submit();

    Assert.Contains("Enter your email and password.", page.Find(".sd-alert").TextContent);
    Assert.Single(Api.Requests);
  }

  [Theory]
  [InlineData(true, 1)]
  [InlineData(false, 0)]
  public void SignIn_OffersRegistrationOnlyWhenOpen(bool open, int links)
  {
    Api.RespondWith(new AccountSettings { RegistrationOpen = open });

    var page = Render<SignIn>();

    page.WaitForAssertion(() => Assert.Equal(links, page.FindAll("a[href='account/register']").Count));
  }

  // ---------- Register ----------

  [Fact]
  public void Register_Closed_ExplainsInsteadOfShowingTheForm()
  {
    Api.RespondWith(new AccountSettings { RegistrationOpen = false });

    var page = Render<Register>();

    page.WaitForAssertion(() => Assert.Contains("Registration is closed", page.Markup));
    Assert.Empty(page.FindAll("form"));
  }

  [Fact]
  public void Register_WeakPassword_ShowsTheRulesUnderThePassword()
  {
    Api.RespondWith(new AccountSettings { RegistrationOpen = true });
    Api.Respond(HttpStatusCode.BadRequest, WeakPassword);
    var page = Render<Register>();
    page.WaitForAssertion(() => page.Find("form"));

    Fill(page, "Email", "ana@example.com");
    Fill(page, "Password", "short");
    Fill(page, "Confirm password", "short");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("at least 8 characters", ErrorOf(page, "Password").TextContent));
    Assert.Contains("one digit", ErrorOf(page, "Password").TextContent);
    Assert.Equal("true", Input(page, "Password").GetAttribute("aria-invalid"));
  }

  [Fact]
  public void Register_TakenEmail_ShowsUnderTheEmail()
  {
    Api.RespondWith(new AccountSettings { RegistrationOpen = true });
    Api.Respond(HttpStatusCode.BadRequest, new { errors = new Dictionary<string, string[]> { ["DuplicateUserName"] = ["Username 'ana@example.com' is already taken."] } });
    var page = Render<Register>();
    page.WaitForAssertion(() => page.Find("form"));

    Fill(page, "Email", "ana@example.com");
    Fill(page, "Password", "Correct-horse-9");
    Fill(page, "Confirm password", "Correct-horse-9");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("already taken", ErrorOf(page, "Email").TextContent));
  }

  [Fact]
  public void Register_DifferentConfirmation_IsCaughtBeforeTheServer()
  {
    Api.RespondWith(new AccountSettings { RegistrationOpen = true });
    var page = Render<Register>();
    page.WaitForAssertion(() => page.Find("form"));

    Fill(page, "Email", "ana@example.com");
    Fill(page, "Password", "Correct-horse-9");
    Fill(page, "Confirm password", "Correct-horse-8");
    page.Find("form").Submit();

    Assert.Contains("don't match", ErrorOf(page, "Confirm password").TextContent);
    Assert.Single(Api.Requests);
  }

  [Fact]
  public void Register_ThenSignsInAndOpensTheDevices()
  {
    Api.RespondWith(new AccountSettings { RegistrationOpen = true });
    Api.Respond(HttpStatusCode.OK);
    Api.Respond(HttpStatusCode.OK);
    var page = Render<Register>();
    page.WaitForAssertion(() => page.Find("form"));

    Fill(page, "Email", "ana@example.com");
    Fill(page, "Password", "Correct-horse-9");
    Fill(page, "Confirm password", "Correct-horse-9");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal(string.Empty, Location));
    Assert.Contains("POST /api/auth/register", Api.Requests);
    Assert.Contains("POST /api/auth/login?useCookies=true", Api.Requests);
  }

  [Fact]
  public void Register_WhenConfirmationIsRequired_AsksToCheckTheEmail()
  {
    Api.RespondWith(new AccountSettings { RegistrationOpen = true });
    Api.Respond(HttpStatusCode.OK);
    Api.Respond(HttpStatusCode.Unauthorized, new { detail = "NotAllowed" });
    var page = Render<Register>();
    page.WaitForAssertion(() => page.Find("form"));

    Fill(page, "Email", "ana@example.com");
    Fill(page, "Password", "Correct-horse-9");
    Fill(page, "Confirm password", "Correct-horse-9");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("account/register-confirmation?email=ana%40example.com", Location));
  }

  // ---------- Forgot and reset password ----------

  [Fact]
  public void ForgotPassword_ConfirmsWithoutSayingWhetherTheAccountExists()
  {
    Api.Respond(HttpStatusCode.OK);
    var page = Render<ForgotPassword>();

    Fill(page, "Email", "ana@example.com");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("If an account with a confirmed email uses ana@example.com", page.Markup));
    Assert.Empty(page.FindAll("form"));
  }

  [Fact]
  public void ForgotPassword_ServerError_KeepsTheForm()
  {
    Api.Respond(HttpStatusCode.InternalServerError);
    var page = Render<ForgotPassword>();

    Fill(page, "Email", "ana@example.com");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Single(page.FindAll(".sd-alert--danger")));
    Assert.Single(page.FindAll("form"));
  }

  [Fact]
  public void ResetPassword_SetsTheNewPassword()
  {
    Api.Respond(HttpStatusCode.OK);
    Navigation.NavigateTo("account/reset-password?email=ana%40example.com&code=abc");
    var page = Render<ResetPassword>();

    Fill(page, "New password", "Brand-new-Passw0rd");
    Fill(page, "Confirm new password", "Brand-new-Passw0rd");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("Your password is changed", page.Markup));
  }

  [Fact]
  public void ResetPassword_UsedLink_OffersANewOne()
  {
    Api.Respond(HttpStatusCode.BadRequest, new { errors = new Dictionary<string, string[]> { ["InvalidToken"] = ["Invalid token."] } });
    Navigation.NavigateTo("account/reset-password?email=ana%40example.com&code=abc");
    var page = Render<ResetPassword>();

    Fill(page, "New password", "Brand-new-Passw0rd");
    Fill(page, "Confirm new password", "Brand-new-Passw0rd");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("invalid, already used or expired", page.Find(".sd-alert").TextContent));
    Assert.Equal("account/forgot-password", page.Find(".sd-alert a").GetAttribute("href"));
  }

  [Fact]
  public void ResetPassword_WeakPassword_ShowsUnderTheField()
  {
    Api.Respond(HttpStatusCode.BadRequest, WeakPassword);
    Navigation.NavigateTo("account/reset-password?email=ana%40example.com&code=abc");
    var page = Render<ResetPassword>();

    Fill(page, "New password", "short");
    Fill(page, "Confirm new password", "short");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("at least 8 characters", ErrorOf(page, "New password").TextContent));
  }

  [Fact]
  public void ResetPassword_IncompleteLink_SaysSo()
  {
    Navigation.NavigateTo("account/reset-password?email=ana%40example.com");

    var page = Render<ResetPassword>();

    Assert.Contains("This link is incomplete", page.Markup);
    Assert.Empty(page.FindAll("form"));
  }

  // ---------- Confirmation ----------

  [Fact]
  public void ResendConfirmation_StartsWithTheEmailFromTheLink()
  {
    Api.Respond(HttpStatusCode.OK);
    Navigation.NavigateTo("account/resend-confirmation?email=ana%40example.com");
    var page = Render<ResendConfirmation>();

    Assert.Equal("ana@example.com", Input(page, "Email").GetAttribute("value"));
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("a new link is on its way", page.Markup));
  }

  [Fact]
  public void ResendConfirmation_ServerError_KeepsTheForm()
  {
    Api.Respond(HttpStatusCode.InternalServerError);
    Navigation.NavigateTo("account/resend-confirmation?email=ana%40example.com");
    var page = Render<ResendConfirmation>();

    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Single(page.FindAll(".sd-alert--danger")));
  }

  [Theory]
  [InlineData("account/email-confirmed", "Email confirmed")]
  [InlineData("account/email-confirmed?failed=true", "This link didn't work")]
  public void EmailConfirmed_ShowsTheOutcome(string address, string heading)
  {
    Navigation.NavigateTo(address);

    var page = Render<EmailConfirmed>();

    Assert.Equal(heading, page.Find("h1").TextContent);
  }

  [Fact]
  public void RegisterConfirmation_LinksToSendingAgain()
  {
    Navigation.NavigateTo("account/register-confirmation?email=ana%40example.com");

    var page = Render<RegisterConfirmation>();

    Assert.Contains("ana@example.com", page.Markup);
    Assert.Equal("account/resend-confirmation?email=ana%40example.com", page.Find(".sd-auth-footer a").GetAttribute("href"));
  }

  [Fact]
  public void LockedOutAndAccessDenied_Explain()
  {
    Assert.Equal("Account locked", Render<LockedOut>().Find("h1").TextContent);
    Assert.Equal("Access denied", Render<AccessDenied>().Find("h1").TextContent);
  }
}
