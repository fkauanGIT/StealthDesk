using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>Two-factor with an authenticator app: setup, the second sign-in step, recovery codes, and turning it off.</summary>
public class TwoFactorTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task Setup_ShowsTheSameKeyAsTextAndQrCode()
  {
    using var server = ServerHost.InMemory();
    await TestAccounts.CreateUserAsync(server, "ana@example.com");
    using var client = await SignedInAsync(server, "ana@example.com");

    var first = await client.GetFromJsonAsync<AuthenticatorSetup>(Routes.Authenticator, Cancel);
    var again = await client.GetFromJsonAsync<AuthenticatorSetup>(Routes.Authenticator, Cancel);
    var status = await client.GetFromJsonAsync<TwoFactorStatus>(Routes.TwoFactor, Cancel);

    Assert.Equal(first!.SharedKey, again!.SharedKey);
    Assert.Matches("^([a-z2-7]{4} )+[a-z2-7]{1,4}$", first.SharedKey);
    Assert.StartsWith("otpauth://totp/StealthDesk:ana%40example.com?secret=", first.AuthenticatorUri, StringComparison.Ordinal);
    Assert.Contains($"secret={first.SharedKey.Replace(" ", string.Empty).ToUpperInvariant()}&", first.AuthenticatorUri, StringComparison.Ordinal);
    Assert.StartsWith("data:image/png;base64,", first.QrCode, StringComparison.Ordinal);
    Assert.True(status!.HasAuthenticator);
    Assert.False(status.Enabled);
  }

  [Fact]
  public async Task Enable_WithAWrongCode_IsRefused()
  {
    using var server = ServerHost.InMemory();
    await TestAccounts.CreateUserAsync(server, "bia@example.com");
    using var client = await SignedInAsync(server, "bia@example.com");
    await client.GetAsync(Routes.Authenticator, Cancel);

    var enable = await client.PostAsJsonAsync(Routes.TwoFactorEnable, new TwoFactorEnable { Code = "000000" }, Cancel);
    var status = await client.GetFromJsonAsync<TwoFactorStatus>(Routes.TwoFactor, Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, enable.StatusCode);
    Assert.Contains("InvalidCode", await enable.Content.ReadAsStringAsync(Cancel));
    Assert.False(status!.Enabled);
  }

  [Fact]
  public async Task Enable_GivesTenRecoveryCodesAndSignsOnlyTheOtherSessionsOut()
  {
    using var server = ServerHost.InMemory().WithServices(CheckSessionsOnEveryRequest);
    await TestAccounts.CreateUserAsync(server, "caio@example.com");
    var here = await TestAccounts.SignInForCookieAsync(server, "caio@example.com");
    var elsewhere = await TestAccounts.SignInForCookieAsync(server, "caio@example.com");
    using var client = TestAccounts.Client(server);
    var setup = await SendAsync(client, HttpMethod.Get, Routes.Authenticator, here);
    var key = (await setup.Content.ReadFromJsonAsync<AuthenticatorSetup>(Cancel))!.SharedKey;
    here = TestAccounts.SessionCookie(setup)!;

    var enable = await SendAsync(client, HttpMethod.Post, Routes.TwoFactorEnable, here, new TwoFactorEnable { Code = Spaced(Totp.Code(key)) });
    var codes = await enable.Content.ReadFromJsonAsync<RecoveryCodeSet>(Cancel);
    var renewed = TestAccounts.SessionCookie(enable)!;

    Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
    Assert.Equal(10, codes!.Codes.Count);
    Assert.Equal(10, codes.Codes.Distinct().Count());
    Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, Routes.CurrentUser, renewed)).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, Routes.CurrentUser, elsewhere)).StatusCode);
  }

  [Fact]
  public async Task WithTwoFactorOn_ThePasswordAloneDoesNotSignIn()
  {
    using var server = ServerHost.InMemory();
    var key = await UserWithTwoFactorAsync(server, "duda@example.com");
    using var client = TestAccounts.Client(server);

    var password = await TestAccounts.SignInAsync(client, "duda@example.com");
    var pending = Cookie(password, "TwoFactorUserId");
    var wrong = await SendAsync(client, HttpMethod.Post, Routes.SignInTwoFactor, pending!, new TwoFactorSignIn { Code = "000000" });
    var right = await SendAsync(client, HttpMethod.Post, Routes.SignInTwoFactor, pending!, new TwoFactorSignIn { Code = Totp.Code(key) });
    var session = TestAccounts.SessionCookie(right);

    Assert.Equal(HttpStatusCode.Unauthorized, password.StatusCode);
    Assert.Contains("RequiresTwoFactor", await password.Content.ReadAsStringAsync(Cancel));
    Assert.Null(TestAccounts.SessionCookie(password));
    Assert.NotNull(pending);
    Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    Assert.Null(TestAccounts.SessionCookie(wrong));
    Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, Routes.CurrentUser, session!)).StatusCode);
  }

  [Fact]
  public async Task SecondStep_WithoutThePasswordFirst_IsRefused()
  {
    using var server = ServerHost.InMemory();
    var key = await UserWithTwoFactorAsync(server, "edu@example.com");
    using var client = TestAccounts.Client(server);

    var code = await client.PostAsJsonAsync(Routes.SignInTwoFactor, new TwoFactorSignIn { Code = Totp.Code(key) }, Cancel);

    Assert.Equal(HttpStatusCode.Unauthorized, code.StatusCode);
    Assert.Null(TestAccounts.SessionCookie(code));
  }

  [Fact]
  public async Task RecoveryCode_SignsInOnlyOnce()
  {
    using var server = ServerHost.InMemory();
    await TestAccounts.CreateUserAsync(server, "fabi@example.com");
    using var client = await SignedInAsync(server, "fabi@example.com");
    var codes = await EnableAsync(client);
    using var signIn = TestAccounts.Client(server);

    var first = await SendAsync(signIn, HttpMethod.Post, Routes.SignInRecoveryCode, await PendingAsync(signIn, "fabi@example.com"),
      new RecoveryCodeSignIn { RecoveryCode = codes[0] });
    var again = await SendAsync(signIn, HttpMethod.Post, Routes.SignInRecoveryCode, await PendingAsync(signIn, "fabi@example.com"),
      new RecoveryCodeSignIn { RecoveryCode = codes[0] });
    var status = await client.GetFromJsonAsync<TwoFactorStatus>(Routes.TwoFactor, Cancel);

    Assert.Equal(HttpStatusCode.OK, first.StatusCode);
    Assert.NotNull(TestAccounts.SessionCookie(first));
    Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    Assert.Equal(9, status!.RecoveryCodesLeft);
  }

  [Fact]
  public async Task RememberedBrowser_SkipsTheCodeUntilItIsForgotten()
  {
    using var server = ServerHost.InMemory();
    var key = await UserWithTwoFactorAsync(server, "gil@example.com");
    using var client = TestAccounts.Client(server);

    var step = await SendAsync(client, HttpMethod.Post, Routes.SignInTwoFactor, await PendingAsync(client, "gil@example.com"),
      new TwoFactorSignIn { Code = Totp.Code(key), RememberBrowser = true });
    var remembered = Cookie(step, "TwoFactorRememberMe")!;
    var session = TestAccounts.SessionCookie(step)!;
    var withRemembered = await SignInWithCookieAsync(client, "gil@example.com", remembered);
    var status = await SendAsync(client, HttpMethod.Get, Routes.TwoFactor, $"{session}; {remembered}");
    var forget = await SendAsync(client, HttpMethod.Post, Routes.ForgetBrowser, $"{session}; {remembered}");

    Assert.NotNull(remembered);
    Assert.Equal(HttpStatusCode.OK, withRemembered.StatusCode);
    Assert.True((await status.Content.ReadFromJsonAsync<TwoFactorStatus>(Cancel))!.IsBrowserRemembered);
    Assert.Equal(HttpStatusCode.NoContent, forget.StatusCode);
    Assert.Contains(forget.Headers.GetValues("Set-Cookie"), x => x.StartsWith("Identity.TwoFactorRememberMe=;", StringComparison.Ordinal));
  }

  [Fact]
  public async Task Disable_LetsThePasswordSignInAgain()
  {
    using var server = ServerHost.InMemory();
    await TestAccounts.CreateUserAsync(server, "hugo@example.com");
    using var client = await SignedInAsync(server, "hugo@example.com");
    await EnableAsync(client);

    var disable = await client.PostAsync(Routes.TwoFactorDisable, null, Cancel);
    var signIn = await TestAccounts.SignInAsync(TestAccounts.Client(server), "hugo@example.com");

    Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
    Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
  }

  [Fact]
  public async Task ResetAuthenticator_TurnsTwoFactorOffWithANewKey()
  {
    using var server = ServerHost.InMemory();
    await TestAccounts.CreateUserAsync(server, "iris@example.com");
    using var client = await SignedInAsync(server, "iris@example.com");
    await EnableAsync(client);
    var before = (await client.GetFromJsonAsync<AuthenticatorSetup>(Routes.Authenticator, Cancel))!.SharedKey;

    var reset = await client.PostAsync(Routes.AuthenticatorReset, null, Cancel);
    var after = (await client.GetFromJsonAsync<AuthenticatorSetup>(Routes.Authenticator, Cancel))!.SharedKey;
    var status = await client.GetFromJsonAsync<TwoFactorStatus>(Routes.TwoFactor, Cancel);

    Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
    Assert.NotEqual(before, after);
    Assert.False(status!.Enabled);
  }

  [Fact]
  public async Task RecoveryCodes_RegenerateOnlyWithTwoFactorOnAndReplaceTheOldOnes()
  {
    using var server = ServerHost.InMemory();
    await TestAccounts.CreateUserAsync(server, "joao@example.com");
    using var client = await SignedInAsync(server, "joao@example.com");

    var refused = await client.PostAsync(Routes.RecoveryCodes, null, Cancel);
    var old = await EnableAsync(client);
    var regenerated = await (await client.PostAsync(Routes.RecoveryCodes, null, Cancel)).Content.ReadFromJsonAsync<RecoveryCodeSet>(Cancel);
    using var signIn = TestAccounts.Client(server);
    var withOld = await SendAsync(signIn, HttpMethod.Post, Routes.SignInRecoveryCode, await PendingAsync(signIn, "joao@example.com"),
      new RecoveryCodeSignIn { RecoveryCode = old[0] });
    var withNew = await SendAsync(signIn, HttpMethod.Post, Routes.SignInRecoveryCode, await PendingAsync(signIn, "joao@example.com"),
      new RecoveryCodeSignIn { RecoveryCode = regenerated!.Codes[0] });

    Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    Assert.Equal(10, regenerated!.Codes.Count);
    Assert.Empty(regenerated.Codes.Intersect(old));
    Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);
    Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
  }

  [Fact]
  public async Task Setup_RequiresSignIn()
  {
    using var server = ServerHost.InMemory();
    using var client = TestAccounts.Client(server);

    Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Routes.TwoFactor, Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Routes.Authenticator, Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(Routes.TwoFactorDisable, null, Cancel)).StatusCode);
  }

  // Identity checks a session against the user's security stamp every 30 minutes; tests can't wait that long.
  private static void CheckSessionsOnEveryRequest(IServiceCollection services) =>
    services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);

  private static async Task<HttpClient> SignedInAsync(ServerHost server, string email)
  {
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
    return client;
  }

  private static async Task<string> UserWithTwoFactorAsync(ServerHost server, string email)
  {
    await TestAccounts.CreateUserAsync(server, email);
    using var client = await SignedInAsync(server, email);
    var key = (await client.GetFromJsonAsync<AuthenticatorSetup>(Routes.Authenticator, Cancel))!.SharedKey;
    await EnableAsync(client, key);
    return key;
  }

  private static async Task<IReadOnlyList<string>> EnableAsync(HttpClient client, string? key = null)
  {
    key ??= (await client.GetFromJsonAsync<AuthenticatorSetup>(Routes.Authenticator, Cancel))!.SharedKey;
    var enable = await client.PostAsJsonAsync(Routes.TwoFactorEnable, new TwoFactorEnable { Code = Totp.Code(key) }, Cancel);
    Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
    return (await enable.Content.ReadFromJsonAsync<RecoveryCodeSet>(Cancel))!.Codes;
  }

  /// <summary>Signs in with the password and returns the cookie that says the code is still missing.</summary>
  private static async Task<string> PendingAsync(HttpClient client, string email)
  {
    var password = await TestAccounts.SignInAsync(client, email);
    Assert.Contains("RequiresTwoFactor", await password.Content.ReadAsStringAsync(Cancel));
    return Cookie(password, "TwoFactorUserId") ?? throw new InvalidOperationException("No two-factor cookie.");
  }

  private static Task<HttpResponseMessage> SignInWithCookieAsync(HttpClient client, string email, string cookie)
  {
    var request = TestAccounts.WithCookie(HttpMethod.Post, $"{Routes.Auth}/login?useCookies=true", cookie);
    request.Content = JsonContent.Create(new { email, password = TestAccounts.Password });
    return client.SendAsync(request, Cancel);
  }

  // Identity names its two-factor cookies "Identity.TwoFactorUserId" and "Identity.TwoFactorRememberMe".
  private static string? Cookie(HttpResponseMessage response, string scheme) =>
    response.Headers.TryGetValues("Set-Cookie", out var values)
      ? values.Select(x => x.Split(';')[0]).FirstOrDefault(x => x.StartsWith($"Identity.{scheme}=", StringComparison.Ordinal) && !x.EndsWith('='))
      : null;

  private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string cookie, object? body = null)
  {
    var request = TestAccounts.WithCookie(method, path, cookie);
    if (body is not null)
    {
      request.Content = JsonContent.Create(body);
    }

    return client.SendAsync(request, Cancel);
  }

  // How apps show a code: "123 456".
  private static string Spaced(string code) => $"{code[..3]} {code[3..]}";
}
