using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.Pages.Account;
using StealthDesk.Web.Client.Pages.Account.Manage;

namespace StealthDesk.Web.Client.Tests;

/// <summary>Signing in with a passkey and managing them, against a fake server and a fake authenticator.</summary>
public class PasskeyPageTests : AccountTestContext
{
  private const string Options = "{\"challenge\":\"abc\"}";

  private static readonly PasskeySummary Laptop = new() { Id = "a1", Name = "Work laptop", CreatedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };
  private static readonly PasskeySummary Phone = new() { Id = "b2", Name = null, CreatedAt = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero), IsBackedUp = true };

  public PasskeyPageTests()
  {
    Services.AddSingleton<SessionGuard>();
    Passkeys.Supported = true;
  }

  // ---------- Signing in ----------

  [Fact]
  public void SignIn_WithoutPasskeySupport_ShowsOnlyThePassword()
  {
    Passkeys.Supported = false;
    Api.RespondWith(new AccountSettings());

    var page = Render<SignIn>();

    page.WaitForAssertion(() => Assert.Single(Api.Requests));
    Assert.DoesNotContain(page.FindAll("button"), x => x.TextContent.Contains("passkey"));
  }

  [Fact]
  public void SignIn_WithAPasskey_GoesWhereTheUserWasHeading()
  {
    Api.RespondWith(new AccountSettings());
    Navigation.NavigateTo("account/sign-in?returnUrl=%2Fdevices%2F42");
    var page = Render<SignIn>();
    var button = page.WaitForElement("button.sd-btn--secondary");
    Api.RespondWith(new { challenge = "abc" });
    Api.Respond(HttpStatusCode.OK);
    Api.RespondWith(new CurrentUser { Email = "ana@example.com" });

    Fill(page, "Email", "ana@example.com");
    button.Click();

    page.WaitForAssertion(() => Assert.Equal("devices/42", Location));
    Assert.Contains("POST /api/auth/passkey/request-options?email=ana%40example.com", Api.Requests);
    Assert.Contains("POST /api/auth/passkey", Api.Requests);
    var body = System.Text.Json.JsonDocument.Parse(Api.Bodies[Api.Requests.ToList().IndexOf("POST /api/auth/passkey")]);
    Assert.Equal("{\"id\":\"credential\"}", body.RootElement.GetProperty("credentialJson").GetString());
    Assert.Equal("abort", Passkeys.Calls[0]);
  }

  [Fact]
  public void SignIn_PasskeyPromptClosed_SaysSoAndStays()
  {
    Passkeys.Answer = new PasskeyOutcome(null, "NotAllowedError");
    Api.RespondWith(new AccountSettings());
    var page = Render<SignIn>();
    var button = page.WaitForElement("button.sd-btn--secondary");
    Api.RespondWith(new { challenge = "abc" });

    button.Click();

    page.WaitForAssertion(() => Assert.Contains("No passkey was used.", page.Find(".sd-alert").TextContent));
    Assert.Contains("check that Bluetooth is on for both devices", page.Find(".sd-alert").TextContent);
    Assert.DoesNotContain("POST /api/auth/passkey", Api.Requests);
  }

  [Fact]
  public void SignIn_PasskeyTheServerDoesNotKnow_SaysSo()
  {
    Api.RespondWith(new AccountSettings());
    Navigation.NavigateTo("account/sign-in");
    var page = Render<SignIn>();
    var button = page.WaitForElement("button.sd-btn--secondary");
    Api.RespondWith(new { challenge = "abc" });
    Api.Respond(HttpStatusCode.Unauthorized, new { detail = "Failed" });

    button.Click();

    page.WaitForAssertion(() => Assert.Contains("This passkey isn't registered here", page.Find(".sd-alert").TextContent));
    Assert.Equal("account/sign-in", Location);
  }

  [Fact]
  public void SignIn_PasskeyPickedFromTheEmailField_SignsIn()
  {
    Passkeys.AutofillSupported = true;
    Api.RespondWith(new AccountSettings());
    Api.RespondWith(new { challenge = "abc" });
    Navigation.NavigateTo("account/sign-in?returnUrl=%2Fdevices%2F42");
    var page = Render<SignIn>();
    page.WaitForAssertion(() => Assert.Contains(Passkeys.Calls, x => x.StartsWith("autofill", StringComparison.Ordinal)));
    Api.Respond(HttpStatusCode.OK);
    Api.RespondWith(new CurrentUser { Email = "ana@example.com" });

    Passkeys.Autofill.SetResult(new PasskeyOutcome("{\"id\":\"picked\"}", null));

    page.WaitForAssertion(() => Assert.Equal("devices/42", Location));
    Assert.Contains("POST /api/auth/passkey", Api.Requests);
    Assert.Equal("username webauthn", Input(page, "Email").GetAttribute("autocomplete"));
  }

  // ---------- Managing ----------

  [Fact]
  public void List_ShowsEachPasskeyWithItsNameOrAPlaceholder()
  {
    Api.RespondWith(new[] { Laptop, Phone });

    var page = Render<Pages.Account.Manage.Passkeys>();

    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".sd-passkey").Count));
    Assert.Equal(["Work laptop", "Unnamed passkey"], page.FindAll(".sd-passkey-name").Select(x => x.TextContent));
    Assert.Equal("Synced", Assert.Single(page.FindAll(".sd-passkey .sd-status")).TextContent);
    Assert.Contains("Added 1 Oct 2026", page.Find(".sd-passkey").TextContent);
    Assert.Equal("account/manage/passkeys/a1", page.FindAll(".sd-passkey a")[0].GetAttribute("href"));
  }

  [Fact]
  public void List_Empty_InvitesToAddOne()
  {
    Api.RespondWith(Array.Empty<PasskeySummary>());

    var page = Render<Pages.Account.Manage.Passkeys>();

    page.WaitForAssertion(() => Assert.Equal("No passkeys yet", page.Find(".sd-empty-title").TextContent));
    Assert.Contains(page.FindAll("button"), x => x.TextContent.Trim() == "Add a passkey");
  }

  [Fact]
  public void List_WithoutBrowserSupport_SaysSoInsteadOfOfferingToAdd()
  {
    Passkeys.Supported = false;
    Api.RespondWith(Array.Empty<PasskeySummary>());

    var page = Render<Pages.Account.Manage.Passkeys>();

    page.WaitForAssertion(() => Assert.Contains("doesn't support passkeys", page.Find(".sd-alert--warning").TextContent));
    Assert.DoesNotContain(page.FindAll("button"), x => x.TextContent.Trim() == "Add a passkey");
  }

  [Fact]
  public void Add_CreatesThePasskeyAndAsksForAName()
  {
    Api.RespondWith(Array.Empty<PasskeySummary>());
    var page = Render<Pages.Account.Manage.Passkeys>();
    page.WaitForAssertion(() => page.FindAll("button").Single(x => x.TextContent.Trim() == "Add a passkey"));
    Api.RespondWith(new { challenge = "abc" });
    Api.RespondWith(new PasskeySummary { Id = "new-id" });

    page.FindAll("button").Single(x => x.TextContent.Trim() == "Add a passkey").Click();

    page.WaitForAssertion(() => Assert.Equal("account/manage/passkeys/new-id?new=true", Location));
    Assert.Equal("create {\"challenge\":\"abc\"}", Passkeys.Calls[0]);
    Assert.Equal(["GET /api/account/passkeys", "POST /api/account/passkeys/creation-options", "POST /api/account/passkeys"], Api.Requests);
  }

  [Fact]
  public void Add_AlreadyOnThisDevice_SaysSo()
  {
    Passkeys.Answer = new PasskeyOutcome(null, "InvalidStateError");
    Api.RespondWith(new[] { Laptop });
    var page = Render<Pages.Account.Manage.Passkeys>();
    page.WaitForAssertion(() => page.FindAll("button").Single(x => x.TextContent.Trim() == "Add a passkey"));
    Api.RespondWith(new { challenge = "abc" });

    page.FindAll("button").Single(x => x.TextContent.Trim() == "Add a passkey").Click();

    page.WaitForAssertion(() => Assert.Contains("This device already has a passkey", page.Find(".sd-alert").TextContent));
    Assert.DoesNotContain("POST /api/account/passkeys", Api.Requests);
  }

  [Fact]
  public void Add_RefusedByTheServer_ShowsWhy()
  {
    Api.RespondWith(Array.Empty<PasskeySummary>());
    var page = Render<Pages.Account.Manage.Passkeys>();
    page.WaitForAssertion(() => page.FindAll("button").Single(x => x.TextContent.Trim() == "Add a passkey"));
    Api.RespondWith(new { challenge = "abc" });
    Api.Respond(HttpStatusCode.BadRequest, new { detail = "The passkey couldn't be added: the origin is wrong." });

    page.FindAll("button").Single(x => x.TextContent.Trim() == "Add a passkey").Click();

    page.WaitForAssertion(() => Assert.Contains("the origin is wrong", page.Find(".sd-alert").TextContent));
  }

  [Fact]
  public void Remove_AsksFirstThenTakesItOffTheList()
  {
    Api.RespondWith(new[] { Laptop, Phone });
    var page = Render<Pages.Account.Manage.Passkeys>();
    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".sd-passkey").Count));
    Api.Respond(HttpStatusCode.NoContent);

    page.FindAll(".sd-passkey button")[0].Click();
    Assert.Contains("\"Work laptop\" will no longer sign you in.", page.Find(".sd-dialog").TextContent);
    page.Find(".sd-dialog .sd-btn--danger").Click();

    page.WaitForAssertion(() => Assert.Single(page.FindAll(".sd-passkey")));
    Assert.Contains("DELETE /api/account/passkeys/a1", Api.Requests);
    Assert.Contains("\"Work laptop\" was removed.", page.Find(".sd-alert").TextContent);
    Assert.Empty(page.FindAll(".sd-dialog"));
  }

  [Fact]
  public void Remove_Cancelled_KeepsThePasskey()
  {
    Api.RespondWith(new[] { Laptop });
    var page = Render<Pages.Account.Manage.Passkeys>();
    page.WaitForAssertion(() => page.Find(".sd-passkey"));

    page.Find(".sd-passkey button").Click();
    page.FindAll(".sd-dialog button").Single(x => x.TextContent == "Cancel").Click();

    Assert.Empty(page.FindAll(".sd-dialog"));
    Assert.Single(page.FindAll(".sd-passkey"));
    Assert.DoesNotContain(Api.Requests, x => x.StartsWith("DELETE", StringComparison.Ordinal));
  }

  [Fact]
  public void Rename_NewPasskey_AsksForAFirstNameAndSavesIt()
  {
    Navigation.NavigateTo("account/manage/passkeys/a1?new=true");
    Api.RespondWith(new[] { Laptop with { Name = null } });
    var page = Render<RenamePasskey>(x => x.Add(p => p.Id, "a1"));
    page.WaitForAssertion(() => Assert.Contains("Passkey added.", page.Find(".sd-alert").TextContent));
    Api.RespondWith(Laptop);

    Fill(page, "Name", "Work laptop");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("account/manage/passkeys", Location));
    Assert.Contains("PUT /api/account/passkeys/a1", Api.Requests);
    Assert.Contains("\"name\":\"Work laptop\"", Api.Bodies[^1]);
  }

  [Fact]
  public void Rename_EmptyName_IsCaughtWithoutCallingTheServer()
  {
    Api.RespondWith(new[] { Laptop });
    var page = Render<RenamePasskey>(x => x.Add(p => p.Id, "a1"));
    page.WaitForAssertion(() => Assert.Equal("Work laptop", Input(page, "Name").GetAttribute("value")));

    Fill(page, "Name", "  ");
    page.Find("form").Submit();

    Assert.Equal("Enter a name.", ErrorOf(page, "Name").TextContent);
    Assert.Single(Api.Requests);
  }

  [Fact]
  public void Rename_UnknownPasskey_SaysItIsGone()
  {
    Api.RespondWith(new[] { Laptop });

    var page = Render<RenamePasskey>(x => x.Add(p => p.Id, "zz"));

    page.WaitForAssertion(() => Assert.Equal("Passkey not found", page.Find(".sd-empty-title").TextContent));
  }
}
