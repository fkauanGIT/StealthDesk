using System.Net;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Accounts;

namespace StealthDesk.Server.Tests;

/// <summary>Signing in with an external provider, creating the account on the first visit, linking and unlinking.</summary>
public class ExternalLoginTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task Settings_WithNoProviderConfigured_OfferNone()
  {
    using var server = ServerHost.InMemory();
    using var client = TestAccounts.Client(server);

    var settings = await client.GetFromJsonAsync<AccountSettings>(Routes.AuthSettings, Cancel);

    Assert.Empty(settings!.ExternalProviders);
  }

  [Fact]
  public async Task Settings_OfferOnlyTheConfiguredProviders()
  {
    using var server = ServerHost.InMemory()
      .With("Accounts:MicrosoftClientId", "client-id")
      .With("Accounts:MicrosoftClientSecret", "client-secret")
      .With("Accounts:GitHubClientId", "client-id");
    using var client = TestAccounts.Client(server);

    var settings = await client.GetFromJsonAsync<AccountSettings>(Routes.AuthSettings, Cancel);

    // GitHub has no secret, so it stays off.
    var provider = Assert.Single(settings!.ExternalProviders);
    Assert.Equal("Microsoft", provider.Scheme);
  }

  [Fact]
  public async Task Challenge_GoesToTheProviderWithThisServersClientId()
  {
    using var server = ServerHost.InMemory()
      .With("Accounts:GitHubClientId", "github-client")
      .With("Accounts:GitHubClientSecret", "secret");
    using var browser = TestProvider.Browser(server);

    var challenge = await browser.GetAsync(Routes.ExternalSignIn("GitHub"), Cancel);
    var location = challenge.Headers.Location!.ToString();

    Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
    Assert.StartsWith("https://github.com/login/oauth/authorize?", location, StringComparison.Ordinal);
    Assert.Contains("client_id=github-client", location, StringComparison.Ordinal);
    Assert.Contains("user%3Aemail", location, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownProvider_IsNotFound()
  {
    using var server = ServerHost.InMemory();
    using var browser = TestProvider.Browser(server);

    Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(Routes.ExternalSignIn("GitHub"), Cancel)).StatusCode);
  }

  [Fact]
  public async Task FirstSignIn_CreatesTheAccountFollowingTheRegistrationRules()
  {
    var provider = new TestProvider();
    using var server = provider.AddTo(ServerHost.InMemory());
    using var browser = TestProvider.Browser(server);

    var landing = await TestProvider.FollowAsync(browser, $"{Routes.ExternalSignIn(TestProvider.Scheme)}?returnUrl=%2Fdevices");
    var pending = await browser.GetFromJsonAsync<PendingExternalLogin>(Routes.PendingExternalLogin, Cancel);
    var register = await browser.PostAsJsonAsync(Routes.ExternalRegistration, new ExternalRegistration { Email = pending!.Email! }, Cancel);
    var me = await browser.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel);

    Assert.Equal("/account/external-login?returnUrl=%2Fdevices", landing);
    Assert.Equal(TestProvider.DisplayName, pending.ProviderDisplayName);
    Assert.True((await register.Content.ReadFromJsonAsync<ExternalRegistrationResult>(Cancel))!.SignedIn);
    Assert.Equal("ana@provider.example", me!.Email);
    Assert.True(me.IsServerAdministrator);
    var profile = await browser.GetFromJsonAsync<AccountProfile>(Routes.AccountProfile, Cancel);
    Assert.False(profile!.HasPassword);
  }

  [Fact]
  public async Task NextSignIn_GoesStraightIn()
  {
    var provider = new TestProvider();
    using var server = provider.AddTo(ServerHost.InMemory());
    using (var first = TestProvider.Browser(server))
    {
      await TestProvider.FollowAsync(first, Routes.ExternalSignIn(TestProvider.Scheme));
      await first.PostAsJsonAsync(Routes.ExternalRegistration, new ExternalRegistration { Email = "ana@provider.example" }, Cancel);
    }

    using var browser = TestProvider.Browser(server);
    var landing = await TestProvider.FollowAsync(browser, $"{Routes.ExternalSignIn(TestProvider.Scheme)}?returnUrl=%2Fdevices");

    Assert.Equal("/devices", landing);
    Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(Routes.CurrentUser, Cancel)).StatusCode);
  }

  [Fact]
  public async Task FirstSignIn_WhenRegistrationIsClosed_IsRefused()
  {
    var provider = new TestProvider();
    using var server = provider.AddTo(ServerHost.InMemory());
    await TestAccounts.CreateUserAsync(server, "owner@example.com");
    using var browser = TestProvider.Browser(server);

    await TestProvider.FollowAsync(browser, Routes.ExternalSignIn(TestProvider.Scheme));
    var register = await browser.PostAsJsonAsync(Routes.ExternalRegistration, new ExternalRegistration { Email = "ana@provider.example" }, Cancel);

    Assert.Equal(HttpStatusCode.NotFound, register.StatusCode);
    Assert.Equal(1, await server.WithDbAsync(db => db.Users.CountAsync()));
  }

  [Fact]
  public async Task Register_WithoutComingBackFromTheProvider_IsRefused()
  {
    using var server = new TestProvider().AddTo(ServerHost.InMemory());
    using var browser = TestProvider.Browser(server);

    var register = await browser.PostAsJsonAsync(Routes.ExternalRegistration, new ExternalRegistration { Email = "ana@provider.example" }, Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, register.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(Routes.PendingExternalLogin, Cancel)).StatusCode);
  }

  [Fact]
  public async Task SignIn_WithTwoFactorOn_LeavesTheSecondFactorToTheProvider()
  {
    var provider = new TestProvider();
    using var server = provider.AddTo(ServerHost.InMemory());
    using var setup = TestProvider.Browser(server);
    await TestProvider.FollowAsync(setup, Routes.ExternalSignIn(TestProvider.Scheme));
    await setup.PostAsJsonAsync(Routes.ExternalRegistration, new ExternalRegistration { Email = "ana@provider.example" }, Cancel);
    var key = (await setup.GetFromJsonAsync<AuthenticatorSetup>(Routes.Authenticator, Cancel))!.SharedKey;
    await setup.PostAsJsonAsync(Routes.TwoFactorEnable, new TwoFactorEnable { Code = Totp.Code(key) }, Cancel);
    Assert.True((await setup.GetFromJsonAsync<TwoFactorStatus>(Routes.TwoFactor, Cancel))!.Enabled);

    using var browser = TestProvider.Browser(server);
    var landing = await TestProvider.FollowAsync(browser, $"{Routes.ExternalSignIn(TestProvider.Scheme)}?returnUrl=%2Fdevices");

    Assert.Equal("/devices", landing);
    Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(Routes.CurrentUser, Cancel)).StatusCode);
  }

  [Fact]
  public async Task TurningTheProviderDown_EndsOnTheSignInPage()
  {
    var provider = new TestProvider { Account = null };
    using var server = provider.AddTo(ServerHost.InMemory());
    using var browser = TestProvider.Browser(server);

    var landing = await TestProvider.FollowAsync(browser, Routes.ExternalSignIn(TestProvider.Scheme));

    Assert.Equal("/account/sign-in?external=cancelled", landing);
  }

  [Theory]
  [InlineData("%2F%2Fevil.example")]
  [InlineData("https%3A%2F%2Fevil.example")]
  [InlineData("%2F%5Cevil.example")]
  public async Task ReturnAddress_OutsideTheSite_IsReplacedWithTheHomePage(string returnUrl)
  {
    var provider = new TestProvider();
    using var server = provider.AddTo(ServerHost.InMemory());
    using (var first = TestProvider.Browser(server))
    {
      await TestProvider.FollowAsync(first, Routes.ExternalSignIn(TestProvider.Scheme));
      await first.PostAsJsonAsync(Routes.ExternalRegistration, new ExternalRegistration { Email = "ana@provider.example" }, Cancel);
    }

    using var browser = TestProvider.Browser(server);
    var landing = await TestProvider.FollowAsync(browser, $"{Routes.ExternalSignIn(TestProvider.Scheme)}?returnUrl={returnUrl}");

    Assert.Equal("/", landing);
  }

  [Fact]
  public async Task Link_AddsTheProviderToAPasswordAccount()
  {
    var provider = new TestProvider();
    using var server = provider.AddTo(ServerHost.InMemory()).WithServices(CheckSessionsOnEveryRequest);
    await TestAccounts.CreateUserAsync(server, "bia@example.com");
    using var browser = TestProvider.Browser(server);
    await TestAccounts.SignInAsync(browser, "bia@example.com");

    var landing = await TestProvider.FollowAsync(browser, Routes.LinkLogin(TestProvider.Scheme));
    var logins = await browser.GetFromJsonAsync<LinkedLogins>(Routes.Logins, Cancel);

    Assert.Equal("/account/manage/external-logins?done=linked", landing);
    var linked = Assert.Single(logins!.Linked);
    Assert.Equal(TestProvider.Scheme, linked.Provider);
    Assert.Empty(logins.Available);
    Assert.True(logins.CanRemove);
    Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(Routes.CurrentUser, Cancel)).StatusCode);
  }

  [Fact]
  public async Task Link_ALoginAnotherAccountUses_IsRefused()
  {
    var provider = new TestProvider();
    using var server = provider.AddTo(ServerHost.InMemory());
    using (var owner = TestProvider.Browser(server))
    {
      await TestProvider.FollowAsync(owner, Routes.ExternalSignIn(TestProvider.Scheme));
      await owner.PostAsJsonAsync(Routes.ExternalRegistration, new ExternalRegistration { Email = "ana@provider.example" }, Cancel);
    }

    await TestAccounts.CreateUserAsync(server, "caio@example.com");
    using var browser = TestProvider.Browser(server);
    await TestAccounts.SignInAsync(browser, "caio@example.com");

    var landing = await TestProvider.FollowAsync(browser, Routes.LinkLogin(TestProvider.Scheme));

    Assert.Equal("/account/manage/external-logins?error=taken", landing);
    Assert.Empty((await browser.GetFromJsonAsync<LinkedLogins>(Routes.Logins, Cancel))!.Linked);
  }

  [Fact]
  public async Task Unlink_TheOnlyWayToSignIn_IsRefusedUntilThereIsAnother()
  {
    var provider = new TestProvider();
    using var server = provider.AddTo(ServerHost.InMemory()).WithServices(CheckSessionsOnEveryRequest);
    using var browser = TestProvider.Browser(server);
    await TestProvider.FollowAsync(browser, Routes.ExternalSignIn(TestProvider.Scheme));
    await browser.PostAsJsonAsync(Routes.ExternalRegistration, new ExternalRegistration { Email = "ana@provider.example" }, Cancel);
    var path = Routes.Login(TestProvider.Scheme, "provider-id-1");

    var before = await browser.GetFromJsonAsync<LinkedLogins>(Routes.Logins, Cancel);
    var refused = await browser.DeleteAsync(path, Cancel);
    await browser.PostAsJsonAsync(Routes.AccountPasswordSet, new PasswordSet { NewPassword = "Brand-new-Passw0rd" }, Cancel);
    var removed = await browser.DeleteAsync(path, Cancel);

    Assert.False(before!.CanRemove);
    Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    Assert.Contains("only way to sign in", await refused.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    Assert.Empty((await browser.GetFromJsonAsync<LinkedLogins>(Routes.Logins, Cancel))!.Linked);
  }

  [Fact]
  public async Task Unlink_WithAPasskey_IsAllowed()
  {
    var provider = new TestProvider();
    using var server = provider.AddTo(ServerHost.InMemory());
    using var browser = TestProvider.Browser(server);
    browser.DefaultRequestHeaders.Add("Origin", "http://localhost");
    await TestProvider.FollowAsync(browser, Routes.ExternalSignIn(TestProvider.Scheme));
    await browser.PostAsJsonAsync(Routes.ExternalRegistration, new ExternalRegistration { Email = "ana@provider.example" }, Cancel);
    using var authenticator = new SoftwareAuthenticator();
    var options = await browser.PostAsync(Routes.PasskeyCreationOptions, null, Cancel);
    var credential = authenticator.Create(await options.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(HttpStatusCode.OK, (await browser.PostAsJsonAsync(Routes.Passkeys, new PasskeyCredential { CredentialJson = credential }, Cancel)).StatusCode);

    var logins = await browser.GetFromJsonAsync<LinkedLogins>(Routes.Logins, Cancel);
    var removed = await browser.DeleteAsync(Routes.Login(TestProvider.Scheme, "provider-id-1"), Cancel);

    Assert.True(logins!.CanRemove);
    Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
  }

  // Identity checks a session against the user's security stamp every 30 minutes; tests can't wait that long.
  private static void CheckSessionsOnEveryRequest(IServiceCollection services) =>
    services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);

  [Fact]
  public async Task Logins_RequireSignIn()
  {
    using var server = new TestProvider().AddTo(ServerHost.InMemory());
    using var client = TestAccounts.Client(server);

    Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Routes.Logins, Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync(Routes.Login(TestProvider.Scheme, "x"), Cancel)).StatusCode);
  }
}
