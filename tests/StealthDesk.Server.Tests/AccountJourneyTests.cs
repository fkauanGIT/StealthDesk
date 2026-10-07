using System.Net;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>
/// Users followed through a whole path on PostgreSQL, using only what the web client uses: each issue tested its own
/// step, these check the steps fit together.
/// </summary>
public class AccountJourneyTests
{
  private const string Password = "Choose-a-Passw0rd";

  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task FirstUser_RegistersAndSeesTheirDevices_WhileAnotherTenantSeesNone()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("Accounts:EnablePublicRegistration", "true");
    using var owner = Browser(server);
    await RegisterAndSignInAsync(owner, "owner@example.com");

    // The first registration made the only tenant, which the agent joins.
    var deviceId = Guid.NewGuid();
    await using var agent = await TestAgent.ConnectAsync(server);
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId, "OWNER-PC"))).Accepted);

    using var stranger = Browser(server);
    await RegisterAndSignInAsync(stranger, "stranger@example.com");
    await using var strangerDashboard = await TestDashboard.ConnectAsync(server, await CookieOfAsync(server, "stranger@example.com"));

    var me = await owner.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel);
    var ownerDevices = await owner.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, Cancel);
    var strangerDevices = await stranger.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, Cancel);
    var strangerDevice = await stranger.GetAsync(Routes.Device(deviceId), Cancel);
    await agent.ReportAsync(TestAgent.Report(deviceId, "OWNER-PC"));

    Assert.True(me!.IsServerAdministrator);
    Assert.Equal("OWNER-PC", Assert.Single(ownerDevices!).Name);
    Assert.Empty(strangerDevices!);
    Assert.Equal(HttpStatusCode.NotFound, strangerDevice.StatusCode);
    await Assert.ThrowsAsync<TimeoutException>(() => strangerDashboard.NextChangeAsync(TimeSpan.FromSeconds(2)));
  }

  [Fact]
  public async Task SignedOut_TheApiAndTheHubRefuse()
  {
    using var server = await ServerHost.OnPostgresAsync();
    using var visitor = Browser(server);
    await RegisterAndSignInAsync(visitor, "owner@example.com");
    Assert.Equal(HttpStatusCode.NoContent, (await visitor.PostAsync(Routes.SignOut, null, Cancel)).StatusCode);

    foreach (var path in new[] { Routes.CurrentUser, Routes.Devices, Routes.AccountProfile, Routes.TwoFactor, Routes.Passkeys })
    {
      Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync(path, Cancel)).StatusCode);
    }

    var hub = await Assert.ThrowsAsync<HttpRequestException>(() => TestDashboard.ConnectAsync(server));
    Assert.Equal(HttpStatusCode.Unauthorized, hub.StatusCode);
  }

  [Fact]
  public async Task TwoFactor_LostDevice_SignsInWithARecoveryCodeOnce()
  {
    using var server = await ServerHost.OnPostgresAsync();
    using var browser = Browser(server);
    await RegisterAndSignInAsync(browser, "owner@example.com");
    var key = (await browser.GetFromJsonAsync<AuthenticatorSetup>(Routes.Authenticator, Cancel))!.SharedKey;
    var enabled = await browser.PostAsJsonAsync(Routes.TwoFactorEnable, new TwoFactorEnable { Code = Totp.Code(key) }, Cancel);
    var codes = (await enabled.Content.ReadFromJsonAsync<RecoveryCodeSet>(Cancel))!.Codes;
    await browser.PostAsync(Routes.SignOut, null, Cancel);

    // The phone is gone: password, then a recovery code instead of the app's code.
    var password = await SignInAsync(browser, "owner@example.com");
    var recovered = await browser.PostAsJsonAsync(Routes.SignInRecoveryCode, new RecoveryCodeSignIn { RecoveryCode = codes[0] }, Cancel);
    var devices = await browser.GetAsync(Routes.Devices, Cancel);
    var left = (await browser.GetFromJsonAsync<TwoFactorStatus>(Routes.TwoFactor, Cancel))!.RecoveryCodesLeft;
    await browser.PostAsync(Routes.SignOut, null, Cancel);
    await SignInAsync(browser, "owner@example.com");
    var again = await browser.PostAsJsonAsync(Routes.SignInRecoveryCode, new RecoveryCodeSignIn { RecoveryCode = codes[0] }, Cancel);

    Assert.Equal(HttpStatusCode.Unauthorized, password.StatusCode);
    Assert.Contains("RequiresTwoFactor", await password.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    Assert.Equal(HttpStatusCode.OK, devices.StatusCode);
    Assert.Equal(9, left);
    Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(Routes.CurrentUser, Cancel)).StatusCode);
  }

  [Fact]
  public async Task RequiredConfirmation_SignInIsRefusedUntilTheLinkIsOpened()
  {
    var emails = new CapturedEmails();
    using var server = (await ServerHost.OnPostgresAsync())
      .With("Accounts:EnablePublicRegistration", "true")
      .With("Accounts:RequireConfirmedEmail", "true")
      .WithCapturedEmails(emails);
    using (var first = Browser(server))
    {
      // The first user is the administrator, confirmed without an email.
      await RegisterAndSignInAsync(first, "owner@example.com");
    }

    using var browser = Browser(server);
    Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(browser, "member@example.com")).StatusCode);
    var before = await SignInAsync(browser, "member@example.com");
    var confirm = await browser.GetAsync(CapturedEmails.LinkIn(emails.To("member@example.com")), Cancel);
    var after = await SignInAsync(browser, "member@example.com");

    Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);
    Assert.Contains("NotAllowed", await before.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
    Assert.Equal("/account/email-confirmed", confirm.Headers.Location!.OriginalString);
    Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    Assert.False((await browser.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel))!.IsServerAdministrator);
  }

  /// <summary>Keeps cookies like a browser, and leaves redirects for the test to look at.</summary>
  private static HttpClient Browser(ServerHost server) =>
    server.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

  private static Task<HttpResponseMessage> RegisterAsync(HttpClient browser, string email) =>
    browser.PostAsJsonAsync($"{Routes.Auth}/register", new { email, password = Password }, Cancel);

  private static Task<HttpResponseMessage> SignInAsync(HttpClient browser, string email) =>
    browser.PostAsJsonAsync($"{Routes.Auth}/login?useCookies=true", new { email, password = Password }, Cancel);

  private static async Task RegisterAndSignInAsync(HttpClient browser, string email)
  {
    Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(browser, email)).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await SignInAsync(browser, email)).StatusCode);
  }

  private static async Task<string> CookieOfAsync(ServerHost server, string email)
  {
    using var client = TestAccounts.Client(server);
    return TestAccounts.SessionCookie(await SignInAsync(client, email))!;
  }
}
