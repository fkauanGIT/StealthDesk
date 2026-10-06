using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.Devices;
using StealthDesk.Web.Client.Pages.Account;
using StealthDesk.Web.Client.Pages.Account.Manage;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The account settings pages and the forced password change, against a fake server.</summary>
public class ManageAccountPageTests : AccountTestContext
{
  private readonly FakeLiveUpdates _live = new();

  public ManageAccountPageTests()
  {
    Services.AddSingleton<SessionGuard>();
    Services.AddSingleton<ILiveUpdates>(_live);
  }

  private static AccountProfile Ana(bool confirmed = true, bool hasPassword = true) => new()
  {
    Id = Guid.Parse("0199a7c4-0000-7000-8000-000000000001"),
    Email = "ana@example.com",
    EmailConfirmed = confirmed,
    PhoneNumber = "+55 11 98765-4321",
    HasPassword = hasPassword,
  };

  private static object Errors(string code, string message) =>
    new { errors = new Dictionary<string, string[]> { [code] = [message] } };

  // ---------- Profile ----------

  [Fact]
  public void Profile_ShowsTheAccountAndSavesThePhoneNumber()
  {
    Api.RespondWith(Ana());
    var page = Render<Profile>();
    page.WaitForAssertion(() => Assert.Equal("ana@example.com", Input(page, "Email").GetAttribute("value")));
    Api.RespondWith(Ana() with { PhoneNumber = "+55 21 3333-4444" });

    Assert.True(Input(page, "Email").HasAttribute("disabled"));
    Assert.Equal("0199a7c4-0000-7000-8000-000000000001", Input(page, "User ID").GetAttribute("value"));
    Fill(page, "Phone number", "+55 21 3333-4444");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("Your profile has been updated.", page.Find(".sd-alert").TextContent));
    Assert.Contains("PUT /api/account/profile", Api.Requests);
  }

  [Fact]
  public void Profile_InvalidPhoneNumber_ShowsUnderTheField()
  {
    Api.RespondWith(Ana());
    var page = Render<Profile>();
    page.WaitForAssertion(() => Input(page, "Phone number"));
    Api.Respond(HttpStatusCode.BadRequest, Errors("InvalidPhoneNumber", "This isn't a valid phone number."));

    Fill(page, "Phone number", "call me");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("This isn't a valid phone number.", ErrorOf(page, "Phone number").TextContent));
    Assert.Empty(page.FindAll(".sd-alert"));
  }

  [Fact]
  public void Profile_ExpiredSession_SendsTheUserToSignInAndBack()
  {
    Navigation.NavigateTo("account/manage");
    Api.Respond(HttpStatusCode.Unauthorized);

    Render<Profile>();

    Assert.Equal("account/sign-in?returnUrl=%2Faccount%2Fmanage", Location);
  }

  // ---------- Email ----------

  [Fact]
  public void Email_Unconfirmed_ShowsItAndSendsTheVerificationAgain()
  {
    Api.RespondWith(Ana(confirmed: false));
    var page = Render<Email>();
    page.WaitForAssertion(() => Assert.Equal("Not confirmed", page.Find(".sd-status").TextContent));
    Api.Respond(HttpStatusCode.OK);

    page.FindAll("button").Single(x => x.TextContent.Contains("Send verification email")).Click();

    page.WaitForAssertion(() => Assert.Contains("Verification email sent.", page.Find(".sd-alert").TextContent));
    Assert.Contains("POST /api/auth/resendConfirmationEmail", Api.Requests);
  }

  [Fact]
  public void Email_Confirmed_HasNoVerificationButton()
  {
    Api.RespondWith(Ana());
    var page = Render<Email>();

    page.WaitForAssertion(() => Assert.Equal("Confirmed", page.Find(".sd-status").TextContent));
    Assert.DoesNotContain(page.FindAll("button"), x => x.TextContent.Contains("verification"));
  }

  [Fact]
  public void Email_Change_SendsALinkToTheNewAddress()
  {
    Api.RespondWith(Ana());
    var page = Render<Email>();
    page.WaitForAssertion(() => Input(page, "New email"));
    Api.Respond(HttpStatusCode.OK);

    Fill(page, "New email", " ana.new@example.com ");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("We sent a confirmation link to ana.new@example.com.", page.Find(".sd-alert").TextContent));
    Assert.Contains("POST /api/auth/manage/info", Api.Requests);
  }

  [Fact]
  public void Email_SameAddress_IsRefusedWithoutCallingTheServer()
  {
    Api.RespondWith(Ana());
    var page = Render<Email>();
    page.WaitForAssertion(() => Input(page, "New email"));

    Fill(page, "New email", "ANA@example.com");
    page.Find("form").Submit();

    Assert.Equal("This is already your email.", ErrorOf(page, "New email").TextContent);
    Assert.Single(Api.Requests);
  }

  [Fact]
  public void Email_InvalidAddress_ShowsUnderTheField()
  {
    Api.RespondWith(Ana());
    var page = Render<Email>();
    page.WaitForAssertion(() => Input(page, "New email"));
    Api.Respond(HttpStatusCode.BadRequest, Errors("InvalidEmail", "Email 'nope' is invalid."));

    Fill(page, "New email", "nope");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("Email 'nope' is invalid.", ErrorOf(page, "New email").TextContent));
  }

  // ---------- Password ----------

  [Fact]
  public void ChangePassword_Succeeds_AndClearsTheForm()
  {
    Api.RespondWith(Ana());
    var page = Render<ChangePassword>();
    page.WaitForAssertion(() => Input(page, "Current password"));
    Api.Respond(HttpStatusCode.NoContent);

    FillPasswords(page, "Correct-horse-9", "Brand-new-Passw0rd", "Brand-new-Passw0rd");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("Your password has been changed.", page.Find(".sd-alert").TextContent));
    Assert.Contains("POST /api/account/password", Api.Requests);
    Assert.Null(Input(page, "New password").GetAttribute("value"));
  }

  [Fact]
  public void ChangePassword_WrongCurrentPassword_ShowsUnderThatField()
  {
    Api.RespondWith(Ana());
    var page = Render<ChangePassword>();
    page.WaitForAssertion(() => Input(page, "Current password"));
    Api.Respond(HttpStatusCode.BadRequest, Errors("PasswordMismatch", "Incorrect password."));

    FillPasswords(page, "Wrong-horse-9", "Brand-new-Passw0rd", "Brand-new-Passw0rd");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("Incorrect password.", ErrorOf(page, "Current password").TextContent));
    Assert.Null(Input(page, "New password").GetAttribute("aria-invalid"));
  }

  [Fact]
  public void ChangePassword_WeakNewPassword_ShowsUnderTheNewPassword()
  {
    Api.RespondWith(Ana());
    var page = Render<ChangePassword>();
    page.WaitForAssertion(() => Input(page, "Current password"));
    Api.Respond(HttpStatusCode.BadRequest, Errors("PasswordTooShort", "Passwords must be at least 8 characters."));

    FillPasswords(page, "Correct-horse-9", "short", "short");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("Passwords must be at least 8 characters.", ErrorOf(page, "New password").TextContent));
    Assert.Null(Input(page, "Current password").GetAttribute("aria-invalid"));
  }

  [Fact]
  public void ChangePassword_Mismatch_IsCaughtWithoutCallingTheServer()
  {
    Api.RespondWith(Ana());
    var page = Render<ChangePassword>();
    page.WaitForAssertion(() => Input(page, "Current password"));

    FillPasswords(page, "Correct-horse-9", "Brand-new-Passw0rd", "Different-Passw0rd");
    page.Find("form").Submit();

    Assert.Equal("The passwords don't match.", ErrorOf(page, "Confirm new password").TextContent);
    Assert.Single(Api.Requests);
  }

  [Fact]
  public void ChangePassword_WithoutAPassword_GoesToSetOne()
  {
    Api.RespondWith(Ana(hasPassword: false));

    var page = Render<ChangePassword>();

    page.WaitForAssertion(() => Assert.Equal("account/manage/set-password", Location));
  }

  [Fact]
  public void SetPassword_AddsAPasswordToAnExternalOnlyAccount()
  {
    Api.RespondWith(Ana(hasPassword: false));
    var page = Render<SetPassword>();
    page.WaitForAssertion(() => Input(page, "New password"));
    Api.Respond(HttpStatusCode.NoContent);

    Fill(page, "New password", "Brand-new-Passw0rd");
    Fill(page, "Confirm new password", "Brand-new-Passw0rd");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("Your password has been set.", page.Find(".sd-alert").TextContent));
    Assert.Contains("POST /api/account/password/set", Api.Requests);
  }

  [Fact]
  public void SetPassword_WithAPassword_GoesToChangeIt()
  {
    Api.RespondWith(Ana());

    var page = Render<SetPassword>();

    page.WaitForAssertion(() => Assert.Equal("account/manage/password", Location));
  }

  // ---------- Personal data and deletion ----------

  [Fact]
  public void PersonalData_DownloadsFromTheServerAndLinksToDeletion()
  {
    var page = Render<PersonalData>();

    var download = page.Find("a[download]");
    Assert.Equal("api/account/personal-data", download.GetAttribute("href"));
    Assert.Equal("account/manage/delete", page.Find("a.sd-btn--danger").GetAttribute("href"));
  }

  [Fact]
  public void Delete_WrongPassword_KeepsTheAccountAndSaysSo()
  {
    Api.RespondWith(Ana());
    Navigation.NavigateTo("account/manage/delete");
    var page = Render<DeleteAccount>();
    page.WaitForAssertion(() => Input(page, "Password"));
    Api.Respond(HttpStatusCode.BadRequest, Errors("PasswordMismatch", "Incorrect password."));

    Fill(page, "Password", "Wrong-horse-9");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("Incorrect password.", ErrorOf(page, "Password").TextContent));
    Assert.Equal("account/manage/delete", Location);
    Assert.False(_live.Stopped);
  }

  [Fact]
  public void Delete_EmptyPassword_IsCaughtWithoutCallingTheServer()
  {
    Api.RespondWith(Ana());
    var page = Render<DeleteAccount>();
    page.WaitForAssertion(() => Input(page, "Password"));

    page.Find("form").Submit();

    Assert.Equal("Enter your password.", ErrorOf(page, "Password").TextContent);
    Assert.Single(Api.Requests);
  }

  [Fact]
  public async Task Delete_SignsOutAndLeaves()
  {
    Api.RespondWith(Ana());
    var page = Render<DeleteAccount>();
    page.WaitForAssertion(() => Input(page, "Password"));
    Api.Respond(HttpStatusCode.NoContent);

    Fill(page, "Password", "Correct-horse-9");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("account/sign-in?deleted=true", Location));
    Assert.Contains("POST /api/account/delete", Api.Requests);
    Assert.True(_live.Stopped);
    var state = await Services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync();
    Assert.False(state.User.Identity!.IsAuthenticated);
  }

  [Fact]
  public void Delete_ExternalOnlyAccount_AsksForNoPassword()
  {
    Api.RespondWith(Ana(hasPassword: false));
    var page = Render<DeleteAccount>();
    page.WaitForAssertion(() => page.Find("form"));
    Api.Respond(HttpStatusCode.NoContent);

    Assert.Empty(page.FindAll("input[type=password]"));
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("account/sign-in?deleted=true", Location));
  }

  [Fact]
  public void SignIn_AfterDeletion_SaysTheAccountIsGone()
  {
    Api.RespondWith(new AccountSettings());
    Navigation.NavigateTo("account/sign-in?deleted=true");

    var page = Render<SignIn>();

    Assert.Contains("Your account has been deleted.", page.Find(".sd-alert").TextContent);
  }

  // ---------- Forced password change and email change ----------

  [Fact]
  public void PasswordChangeRequired_ChangesItAndGoesToTheDevices()
  {
    Navigation.NavigateTo("account/password-change-required");
    var page = Render<PasswordChangeRequired>();
    Api.Respond(HttpStatusCode.NoContent);
    Api.RespondWith(new CurrentUser { Email = "ana@example.com" });

    FillPasswords(page, "Temporary-Passw0rd", "Brand-new-Passw0rd", "Brand-new-Passw0rd", currentLabel: "Temporary password");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal(string.Empty, Location));
    Assert.Equal(["POST /api/account/password", "GET /api/auth/me"], Api.Requests);
  }

  [Fact]
  public void PasswordChangeRequired_WrongTemporaryPassword_ShowsUnderThatField()
  {
    var page = Render<PasswordChangeRequired>();
    Api.Respond(HttpStatusCode.BadRequest, Errors("PasswordMismatch", "Incorrect password."));

    FillPasswords(page, "Wrong-Passw0rd", "Brand-new-Passw0rd", "Brand-new-Passw0rd", currentLabel: "Temporary password");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("Incorrect password.", ErrorOf(page, "Temporary password").TextContent));
  }

  [Fact]
  public void EmailConfirmed_AfterAChange_SaysTheEmailChanged()
  {
    Navigation.NavigateTo("account/email-confirmed?changed=true");

    var page = Render<EmailConfirmed>();

    Assert.Equal("Email changed", page.Find("h1").TextContent);
  }

  private static void FillPasswords<T>(
    IRenderedComponent<T> page, string current, string password, string confirmation, string currentLabel = "Current password")
    where T : Microsoft.AspNetCore.Components.IComponent
  {
    Fill(page, currentLabel, current);
    Fill(page, "New password", password);
    Fill(page, "Confirm new password", confirmation);
  }
}
