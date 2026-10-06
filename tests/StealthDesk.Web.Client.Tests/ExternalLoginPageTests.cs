using System.Net;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.Pages.Account;
using StealthDesk.Web.Client.Pages.Account.Manage;

namespace StealthDesk.Web.Client.Tests;

/// <summary>Signing in with Microsoft or GitHub, the first visit's account, and linked accounts.</summary>
public class ExternalLoginPageTests : AccountTestContext
{
  private static readonly ExternalProvider GitHub = new() { Scheme = "GitHub", DisplayName = "GitHub" };
  private static readonly ExternalProvider Microsoft = new() { Scheme = "Microsoft", DisplayName = "Microsoft" };

  public ExternalLoginPageTests()
  {
    Services.AddSingleton<SessionGuard>();
  }

  private BunitNavigationManager Browser => (BunitNavigationManager)Navigation;

  private static IEnumerable<string> ProviderButtons<T>(IRenderedComponent<T> page)
    where T : Microsoft.AspNetCore.Components.IComponent =>
    page.FindAll("button").Select(x => x.TextContent.Trim()).Where(x => x.StartsWith("Continue with", StringComparison.Ordinal));

  // ---------- Sign in and register ----------

  [Fact]
  public void SignIn_WithNoProviderConfigured_ShowsNoProviderButton()
  {
    Api.RespondWith(new AccountSettings());

    var page = Render<SignIn>();

    page.WaitForAssertion(() => Assert.Single(Api.Requests));
    Assert.Empty(ProviderButtons(page));
  }

  [Fact]
  public void SignIn_ShowsOneButtonPerConfiguredProvider()
  {
    Api.RespondWith(new AccountSettings { ExternalProviders = [Microsoft, GitHub] });

    var page = Render<SignIn>();

    page.WaitForAssertion(() => Assert.Equal(["Continue with Microsoft", "Continue with GitHub"], ProviderButtons(page)));
  }

  [Fact]
  public void ProviderButton_LeavesForTheServerKeepingTheReturnAddress()
  {
    Api.RespondWith(new AccountSettings { ExternalProviders = [GitHub] });
    Navigation.NavigateTo("account/sign-in?returnUrl=%2Fdevices%2F42");
    var page = Render<SignIn>();
    page.WaitForAssertion(() => Assert.Single(ProviderButtons(page)));

    page.FindAll("button").Single(x => x.TextContent.Trim() == "Continue with GitHub").Click();

    var navigation = Browser.History.First();
    Assert.Equal("api/auth/external/GitHub?returnUrl=%2Fdevices%2F42", navigation.Uri);
    Assert.True(navigation.Options.ForceLoad);
  }

  [Theory]
  [InlineData("cancelled", "The sign-in with the provider was cancelled.")]
  [InlineData("failed", "The provider couldn't sign you in. Try again.")]
  [InlineData("unconfirmed", "Confirm your email address before signing in.")]
  public void SignIn_BackFromTheProvider_SaysWhatHappened(string outcome, string message)
  {
    Api.RespondWith(new AccountSettings());
    Navigation.NavigateTo($"account/sign-in?external={outcome}");

    var page = Render<SignIn>();

    Assert.Equal(message, page.Find(".sd-alert").TextContent.Trim());
  }

  [Fact]
  public void Register_OffersTheProvidersToo()
  {
    Api.RespondWith(new AccountSettings { RegistrationOpen = true, ExternalProviders = [GitHub] });

    var page = Render<Register>();

    page.WaitForAssertion(() => Assert.Equal(["Continue with GitHub"], ProviderButtons(page)));
  }

  // ---------- First visit ----------

  [Fact]
  public void FirstVisit_CreatesTheAccountWithTheProvidersEmailAndSignsIn()
  {
    Navigation.NavigateTo("account/external-login?returnUrl=%2Fdevices%2F42");
    Api.RespondWith(new PendingExternalLogin { ProviderDisplayName = "GitHub", Email = "ana@provider.example" });
    var page = Render<ExternalLogin>();
    page.WaitForAssertion(() => Assert.Equal("ana@provider.example", Input(page, "Email").GetAttribute("value")));
    Api.RespondWith(new ExternalRegistrationResult { SignedIn = true });
    Api.RespondWith(new CurrentUser { Email = "ana@provider.example" });

    Assert.Contains("You signed in with GitHub.", page.Find(".sd-auth-subtitle").TextContent);
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("devices/42", Location));
    Assert.Contains("POST /api/auth/external/register", Api.Requests);
  }

  [Fact]
  public void FirstVisit_NeedingConfirmation_SaysToCheckTheEmail()
  {
    Api.RespondWith(new PendingExternalLogin { ProviderDisplayName = "GitHub" });
    var page = Render<ExternalLogin>();
    page.WaitForAssertion(() => Input(page, "Email"));
    Api.RespondWith(new ExternalRegistrationResult { SignedIn = false });

    Fill(page, "Email", "ana@example.com");
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("account/register-confirmation?email=ana%40example.com", Location));
  }

  [Fact]
  public void FirstVisit_WhenRegistrationIsClosed_SaysSo()
  {
    Api.RespondWith(new PendingExternalLogin { ProviderDisplayName = "GitHub", Email = "ana@provider.example" });
    var page = Render<ExternalLogin>();
    page.WaitForAssertion(() => Input(page, "Email"));
    Api.Respond(HttpStatusCode.NotFound);

    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("Registration is closed", page.Find(".sd-auth-subtitle").TextContent));
    Assert.Empty(page.FindAll("form"));
  }

  [Fact]
  public void FirstVisit_EmailTaken_ShowsUnderTheField()
  {
    Api.RespondWith(new PendingExternalLogin { ProviderDisplayName = "GitHub", Email = "ana@provider.example" });
    var page = Render<ExternalLogin>();
    page.WaitForAssertion(() => Input(page, "Email"));
    Api.Respond(HttpStatusCode.BadRequest, new { errors = new Dictionary<string, string[]> { ["DuplicateEmail"] = ["Email 'ana@provider.example' is already taken."] } });

    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Equal("Email 'ana@provider.example' is already taken.", ErrorOf(page, "Email").TextContent));
  }

  [Fact]
  public void FirstVisit_WithoutComingBackFromAProvider_SaysItExpired()
  {
    Api.Respond(HttpStatusCode.NotFound);

    var page = Render<ExternalLogin>();

    page.WaitForAssertion(() => Assert.Equal("This sign-in expired", page.Find("h1").TextContent));
  }

  // ---------- Linked accounts ----------

  [Fact]
  public void Linked_TheOnlyWayToSignIn_CannotBeUnlinked()
  {
    Api.RespondWith(new LinkedLogins
    {
      Linked = [new LinkedLogin { Provider = "GitHub", DisplayName = "GitHub", ProviderKey = "123" }],
      Available = [Microsoft],
      CanRemove = false,
    });

    var page = Render<ExternalLogins>();

    page.WaitForAssertion(() => Assert.Equal("GitHub", page.Find(".sd-passkey-name").TextContent));
    Assert.True(page.FindAll("button").Single(x => x.TextContent == "Unlink").HasAttribute("disabled"));
    Assert.Contains("This is the only way to sign in", page.Find(".sd-card").TextContent);
  }

  [Fact]
  public void Unlink_RemovesTheLoginAndRefreshesTheList()
  {
    Api.RespondWith(new LinkedLogins
    {
      Linked = [new LinkedLogin { Provider = "GitHub", DisplayName = "GitHub", ProviderKey = "a b" }],
      CanRemove = true,
    });
    var page = Render<ExternalLogins>();
    page.WaitForAssertion(() => page.Find(".sd-passkey"));
    Api.Respond(HttpStatusCode.NoContent);
    Api.RespondWith(new LinkedLogins { Available = [GitHub] });

    page.FindAll("button").Single(x => x.TextContent == "Unlink").Click();

    page.WaitForAssertion(() => Assert.Contains("GitHub is unlinked.", page.Find(".sd-alert").TextContent));
    Assert.Contains("DELETE /api/account/logins/GitHub/a%20b", Api.Requests);
    Assert.Empty(page.FindAll(".sd-passkey"));
  }

  [Fact]
  public void Link_LeavesForTheProvider()
  {
    Api.RespondWith(new LinkedLogins { Available = [GitHub] });
    var page = Render<ExternalLogins>();
    page.WaitForAssertion(() => page.FindAll("button").Single(x => x.TextContent.Trim() == "Link GitHub"));

    page.FindAll("button").Single(x => x.TextContent.Trim() == "Link GitHub").Click();

    var navigation = Browser.History.First();
    Assert.Equal("api/account/logins/link/GitHub", navigation.Uri);
    Assert.True(navigation.Options.ForceLoad);
  }

  [Theory]
  [InlineData("done=linked", "The account is linked.")]
  [InlineData("error=taken", "already linked to another StealthDesk user")]
  [InlineData("error=failed", "couldn't link the account")]
  public void Linked_BackFromTheProvider_SaysWhatHappened(string query, string message)
  {
    Navigation.NavigateTo($"account/manage/external-logins?{query}");
    Api.RespondWith(new LinkedLogins());

    var page = Render<ExternalLogins>();

    page.WaitForAssertion(() => Assert.Contains(message, page.Find(".sd-alert").TextContent));
  }
}
