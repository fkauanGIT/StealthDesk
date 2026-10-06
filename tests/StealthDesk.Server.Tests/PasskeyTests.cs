using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>Passkeys: registering one, signing in with it, renaming and removing it.</summary>
public class PasskeyTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task Register_AddsThePasskeyToTheList()
  {
    using var server = (await ServerHost.OnPostgresAsync());
    await TestAccounts.CreateUserAsync(server, "ana@example.com");
    using var browser = await SignedInBrowserAsync(server, "ana@example.com");
    using var authenticator = new SoftwareAuthenticator();

    var added = await RegisterAsync(browser, authenticator);
    var list = await browser.GetFromJsonAsync<List<PasskeySummary>>(Routes.Passkeys, Cancel);

    Assert.Equal(HttpStatusCode.OK, added.StatusCode);
    var passkey = Assert.Single(list!);
    Assert.Equal(authenticator.CredentialIdBase64Url, passkey.Id);
    Assert.Null(passkey.Name);
  }

  [Fact]
  public async Task Register_FromAnotherSite_IsRefused()
  {
    using var server = (await ServerHost.OnPostgresAsync());
    await TestAccounts.CreateUserAsync(server, "bia@example.com");
    using var browser = await SignedInBrowserAsync(server, "bia@example.com");
    using var phishing = new SoftwareAuthenticator("http://stealthdesk.example.evil");

    var added = await RegisterAsync(browser, phishing);
    var list = await browser.GetFromJsonAsync<List<PasskeySummary>>(Routes.Passkeys, Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, added.StatusCode);
    Assert.Empty(list!);
  }

  [Fact]
  public async Task AddingRenamingAndRemoving_KeepEverySession()
  {
    // Passkeys don't change the security stamp, unlike a new password: other devices stay signed in.
    using var server = (await ServerHost.OnPostgresAsync()).WithServices(CheckSessionsOnEveryRequest);
    await TestAccounts.CreateUserAsync(server, "caio@example.com");
    using var browser = await SignedInBrowserAsync(server, "caio@example.com");
    using var elsewhere = await SignedInBrowserAsync(server, "caio@example.com");
    using var authenticator = new SoftwareAuthenticator();
    var path = Routes.Passkey(authenticator.CredentialIdBase64Url);

    await RegisterAsync(browser, authenticator);
    await browser.PutAsJsonAsync(path, new PasskeyRename { Name = "Phone" }, Cancel);
    var removed = await browser.DeleteAsync(path, Cancel);

    Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(Routes.CurrentUser, Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await elsewhere.GetAsync(Routes.CurrentUser, Cancel)).StatusCode);
  }

  [Fact]
  public async Task RegisteredPasskey_SignsTheUserIn()
  {
    using var server = (await ServerHost.OnPostgresAsync());
    var user = await TestAccounts.CreateUserAsync(server, "duda@example.com");
    using var authenticator = new SoftwareAuthenticator();
    using (var setup = await SignedInBrowserAsync(server, "duda@example.com"))
    {
      await RegisterAsync(setup, authenticator);
    }

    using var browser = Browser(server);
    var signIn = await SignInAsync(browser, authenticator);
    var me = await browser.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel);

    Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
    Assert.Equal(user.Id, me!.Id);
  }

  [Fact]
  public async Task RequestOptions_WithAnEmail_NameThatUsersPasskeys()
  {
    using var server = (await ServerHost.OnPostgresAsync());
    await TestAccounts.CreateUserAsync(server, "edu@example.com");
    using var authenticator = new SoftwareAuthenticator();
    using (var setup = await SignedInBrowserAsync(server, "edu@example.com"))
    {
      await RegisterAsync(setup, authenticator);
    }

    using var browser = Browser(server);
    var options = JsonNode.Parse(await (await browser.PostAsync($"{Routes.PasskeyRequestOptions}?email=edu@example.com", null, Cancel)).Content.ReadAsStringAsync(Cancel))!;
    var anyone = JsonNode.Parse(await (await browser.PostAsync(Routes.PasskeyRequestOptions, null, Cancel)).Content.ReadAsStringAsync(Cancel))!;

    Assert.Equal(authenticator.CredentialIdBase64Url, options["allowCredentials"]![0]!["id"]!.GetValue<string>());
    Assert.True(anyone["allowCredentials"] is null || anyone["allowCredentials"]!.AsArray().Count == 0);
  }

  [Fact]
  public async Task RemovedPasskey_NoLongerSignsIn()
  {
    using var server = (await ServerHost.OnPostgresAsync());
    await TestAccounts.CreateUserAsync(server, "fabi@example.com");
    using var authenticator = new SoftwareAuthenticator();
    using var setup = await SignedInBrowserAsync(server, "fabi@example.com");
    await RegisterAsync(setup, authenticator);

    var removed = await setup.DeleteAsync(Routes.Passkey(authenticator.CredentialIdBase64Url), Cancel);
    using var browser = Browser(server);
    var signIn = await SignInAsync(browser, authenticator);

    Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    Assert.Empty((await setup.GetFromJsonAsync<List<PasskeySummary>>(Routes.Passkeys, Cancel))!);
    Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(Routes.CurrentUser, Cancel)).StatusCode);
  }

  [Fact]
  public async Task Rename_ChangesTheNameShownInTheList()
  {
    using var server = (await ServerHost.OnPostgresAsync());
    await TestAccounts.CreateUserAsync(server, "gil@example.com");
    using var authenticator = new SoftwareAuthenticator();
    using var browser = await SignedInBrowserAsync(server, "gil@example.com");
    await RegisterAsync(browser, authenticator);
    var path = Routes.Passkey(authenticator.CredentialIdBase64Url);

    var renamed = await browser.PutAsJsonAsync(path, new PasskeyRename { Name = "  Work laptop  " }, Cancel);
    var empty = await browser.PutAsJsonAsync(path, new PasskeyRename { Name = " " }, Cancel);
    var unknown = await browser.PutAsJsonAsync(Routes.Passkey("AAAA"), new PasskeyRename { Name = "Phone" }, Cancel);
    var list = await browser.GetFromJsonAsync<List<PasskeySummary>>(Routes.Passkeys, Cancel);

    Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    Assert.Contains("InvalidPasskeyName", await empty.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    Assert.Equal("Work laptop", Assert.Single(list!).Name);
  }

  [Fact]
  public async Task AnotherUsersPasskey_CanNeitherBeRenamedNorRemoved()
  {
    using var server = (await ServerHost.OnPostgresAsync());
    await TestAccounts.CreateUserAsync(server, "hugo@example.com");
    await TestAccounts.CreateUserAsync(server, "iris@example.com");
    using var authenticator = new SoftwareAuthenticator();
    using var owner = await SignedInBrowserAsync(server, "hugo@example.com");
    await RegisterAsync(owner, authenticator);
    using var other = await SignedInBrowserAsync(server, "iris@example.com");
    var path = Routes.Passkey(authenticator.CredentialIdBase64Url);

    var rename = await other.PutAsJsonAsync(path, new PasskeyRename { Name = "Mine now" }, Cancel);
    var remove = await other.DeleteAsync(path, Cancel);

    Assert.Equal(HttpStatusCode.NotFound, rename.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, remove.StatusCode);
    Assert.Null(Assert.Single((await owner.GetFromJsonAsync<List<PasskeySummary>>(Routes.Passkeys, Cancel))!).Name);
  }

  [Fact]
  public async Task PasskeySignIn_NeedsNoSecondFactor()
  {
    using var server = (await ServerHost.OnPostgresAsync());
    await TestAccounts.CreateUserAsync(server, "joao@example.com");
    using var authenticator = new SoftwareAuthenticator();
    using (var setup = await SignedInBrowserAsync(server, "joao@example.com"))
    {
      var key = (await setup.GetFromJsonAsync<AuthenticatorSetup>(Routes.Authenticator, Cancel))!.SharedKey;
      Assert.Equal(HttpStatusCode.OK, (await setup.PostAsJsonAsync(Routes.TwoFactorEnable, new TwoFactorEnable { Code = Totp.Code(key) }, Cancel)).StatusCode);
      await RegisterAsync(setup, authenticator);
    }

    using var browser = Browser(server);
    var password = await TestAccounts.SignInAsync(browser, "joao@example.com");
    using var other = Browser(server);
    var passkey = await SignInAsync(other, authenticator);

    Assert.Contains("RequiresTwoFactor", await password.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(HttpStatusCode.OK, passkey.StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await other.GetAsync(Routes.CurrentUser, Cancel)).StatusCode);
  }

  [Theory]
  [InlineData(false, false)]
  [InlineData(true, true)]
  public async Task PasskeySignIn_KeepsTheSessionAfterTheBrowserClosesOnlyWhenSetTo(bool persist, bool expectExpiry)
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("Accounts:PersistPasskeySignIn", persist.ToString());
    await TestAccounts.CreateUserAsync(server, "kaue@example.com");
    using var authenticator = new SoftwareAuthenticator();
    using (var setup = await SignedInBrowserAsync(server, "kaue@example.com"))
    {
      await RegisterAsync(setup, authenticator);
    }

    using var browser = Browser(server);
    var signIn = await SignInAsync(browser, authenticator);
    var cookie = signIn.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal));

    Assert.Equal(expectExpiry, cookie.Contains("expires=", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public async Task Passkeys_WorkOnTheInMemoryDatabaseUsedInDevelopment()
  {
    using var server = ServerHost.InMemory();
    await TestAccounts.CreateUserAsync(server, "lara@example.com");
    using var authenticator = new SoftwareAuthenticator();
    using var setup = await SignedInBrowserAsync(server, "lara@example.com");

    var added = await RegisterAsync(setup, authenticator);
    var list = await setup.GetFromJsonAsync<List<PasskeySummary>>(Routes.Passkeys, Cancel);
    using var browser = Browser(server);
    var signIn = await SignInAsync(browser, authenticator);

    Assert.Equal(HttpStatusCode.OK, added.StatusCode);
    Assert.Equal(authenticator.CredentialIdBase64Url, Assert.Single(list!).Id);
    Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
  }

  [Fact]
  public async Task Passkeys_RequireSignIn()
  {
    using var server = (await ServerHost.OnPostgresAsync());
    using var browser = Browser(server);

    Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(Routes.Passkeys, Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync(Routes.PasskeyCreationOptions, null, Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await browser.DeleteAsync(Routes.Passkey("AAAA"), Cancel)).StatusCode);
  }

  // Identity checks a session against the user's security stamp every 30 minutes; tests can't wait that long.
  private static void CheckSessionsOnEveryRequest(IServiceCollection services) =>
    services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);

  /// <summary>A client that keeps cookies and sends the page's origin, as a browser's fetch() does.</summary>
  private static HttpClient Browser(ServerHost server)
  {
    var client = server.CreateClient();
    client.DefaultRequestHeaders.Add("Origin", "http://localhost");
    return client;
  }

  private static async Task<HttpClient> SignedInBrowserAsync(ServerHost server, string email)
  {
    var browser = Browser(server);
    Assert.Equal(HttpStatusCode.OK, (await TestAccounts.SignInAsync(browser, email)).StatusCode);
    return browser;
  }

  private static async Task<HttpResponseMessage> RegisterAsync(HttpClient browser, SoftwareAuthenticator authenticator)
  {
    var options = await browser.PostAsync(Routes.PasskeyCreationOptions, null, Cancel);
    Assert.Equal(HttpStatusCode.OK, options.StatusCode);
    var credential = authenticator.Create(await options.Content.ReadAsStringAsync(Cancel));
    return await browser.PostAsJsonAsync(Routes.Passkeys, new PasskeyCredential { CredentialJson = credential }, Cancel);
  }

  private static async Task<HttpResponseMessage> SignInAsync(HttpClient browser, SoftwareAuthenticator authenticator)
  {
    var options = await browser.PostAsync(Routes.PasskeyRequestOptions, null, Cancel);
    Assert.Equal(HttpStatusCode.OK, options.StatusCode);
    var credential = authenticator.Get(await options.Content.ReadAsStringAsync(Cancel));
    return await browser.PostAsJsonAsync(Routes.SignInPasskey, new PasskeyCredential { CredentialJson = credential }, Cancel);
  }
}
