using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Accounts;

namespace StealthDesk.Server.Tests;

/// <summary>Signing in with the Identity endpoints: cookies for browsers, bearer tokens when enabled.</summary>
public class AuthenticationTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task CookieSignIn_ThenMe_ReturnsTheUserAndTenant()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var user = await TestAccounts.CreateUserAsync(server, "ana@example.com");
    using var client = TestAccounts.Client(server);

    var signIn = await TestAccounts.SignInAsync(client, "ana@example.com");
    var cookie = TestAccounts.SessionCookie(signIn);
    var me = await client.SendAsync(TestAccounts.WithCookie(HttpMethod.Get, Routes.CurrentUser, cookie!), Cancel);

    Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
    Assert.NotNull(cookie);
    var current = await me.Content.ReadFromJsonAsync<CurrentUser>(Cancel);
    Assert.Equal(user.Id, current!.Id);
    Assert.Equal("ana@example.com", current.Email);
    Assert.Equal(user.TenantId, current.TenantId);
    Assert.Equal(TestTenants.Name, current.TenantName);
  }

  [Fact]
  public async Task Me_WithoutSignIn_IsUnauthorizedNotARedirect()
  {
    using var server = ServerHost.InMemory();
    using var client = TestAccounts.Client(server);

    var me = await client.GetAsync(Routes.CurrentUser, Cancel);

    Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
  }

  [Fact]
  public async Task WrongPassword_IsRefusedAndCountsTowardLockout()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await TestAccounts.CreateUserAsync(server, "bruno@example.com");
    using var client = TestAccounts.Client(server);

    var refused = await TestAccounts.SignInAsync(client, "bruno@example.com", "Wrong-password-1");

    Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    Assert.Null(TestAccounts.SessionCookie(refused));
    var failures = await server.WithDbAsync(db => db.Users.Where(x => x.Email == "bruno@example.com").Select(x => x.AccessFailedCount).SingleAsync());
    Assert.Equal(1, failures);
  }

  [Fact]
  public async Task RepeatedWrongPasswords_LockTheAccount()
  {
    using var server = ServerHost.InMemory();
    await TestAccounts.CreateUserAsync(server, "carla@example.com");
    using var client = TestAccounts.Client(server);
    var attempts = server.Services.GetRequiredService<IOptions<IdentityOptions>>().Value.Lockout.MaxFailedAccessAttempts;

    for (var i = 0; i < attempts; i++)
    {
      await TestAccounts.SignInAsync(client, "carla@example.com", "Wrong-password-1");
    }

    var rightPassword = await TestAccounts.SignInAsync(client, "carla@example.com");
    Assert.Equal(HttpStatusCode.Unauthorized, rightPassword.StatusCode);
    Assert.Contains("LockedOut", await rightPassword.Content.ReadAsStringAsync(Cancel));
  }

  [Fact]
  public async Task SignIn_RecordsTheLastSignIn()
  {
    using var server = ServerHost.InMemory();
    await TestAccounts.CreateUserAsync(server, "dani@example.com");

    await TestAccounts.SignInForCookieAsync(server, "dani@example.com");

    var lastSignIn = await server.WithDbAsync(db => db.Users.Select(x => x.LastSignIn).SingleAsync());
    Assert.NotNull(lastSignIn);
  }

  [Fact]
  public async Task SignOut_EndsTheSession()
  {
    using var server = ServerHost.InMemory();
    await TestAccounts.CreateUserAsync(server, "edu@example.com");
    var cookie = await TestAccounts.SignInForCookieAsync(server, "edu@example.com");
    using var client = TestAccounts.Client(server);

    var signOut = await client.SendAsync(TestAccounts.WithCookie(HttpMethod.Post, Routes.SignOut, cookie), Cancel);

    Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
    Assert.Contains(signOut.Headers.GetValues("Set-Cookie"), x => x.StartsWith(".AspNetCore.Identity.Application=;", StringComparison.Ordinal));
  }

  [Fact]
  public async Task BearerSignIn_IsRefusedWhenDisabled()
  {
    using var server = ServerHost.InMemory();
    await TestAccounts.CreateUserAsync(server, "fabi@example.com");
    using var client = TestAccounts.Client(server);

    var login = await client.PostAsJsonAsync($"{Routes.Auth}/login", new { email = "fabi@example.com", password = TestAccounts.Password }, Cancel);
    var refresh = await client.PostAsJsonAsync($"{Routes.Auth}/refresh", new { refreshToken = "anything" }, Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
  }

  [Fact]
  public async Task BearerSignIn_WhenEnabled_ReturnsTokensThatWorkAndRefresh()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnableBearerLogin", "true");
    var user = await TestAccounts.CreateUserAsync(server, "gabi@example.com");
    using var client = TestAccounts.Client(server);

    var login = await client.PostAsJsonAsync($"{Routes.Auth}/login", new { email = "gabi@example.com", password = TestAccounts.Password }, Cancel);
    var tokens = await login.Content.ReadFromJsonAsync<JsonElement>(Cancel);

    using var me = new HttpRequestMessage(HttpMethod.Get, Routes.CurrentUser);
    me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
    var current = await (await client.SendAsync(me, Cancel)).Content.ReadFromJsonAsync<CurrentUser>(Cancel);

    var refresh = await client.PostAsJsonAsync($"{Routes.Auth}/refresh", new { refreshToken = tokens.GetProperty("refreshToken").GetString() }, Cancel);

    Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    Assert.Equal(user.Id, current!.Id);
    Assert.Equal(user.TenantId, current.TenantId);
    Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    Assert.False(string.IsNullOrEmpty((await refresh.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("accessToken").GetString()));
  }

  [Fact]
  public async Task BearerToken_IsIgnoredWhenDisabled()
  {
    // Same database, so both servers share the keys and the token would be valid if the scheme accepted it.
    var database = Guid.NewGuid().ToString("N");
    using var enabled = ServerHost.InMemory(database).With("Accounts:EnableBearerLogin", "true");
    await TestAccounts.CreateUserAsync(enabled, "hugo@example.com");
    using var enabledClient = TestAccounts.Client(enabled);
    var login = await enabledClient.PostAsJsonAsync($"{Routes.Auth}/login", new { email = "hugo@example.com", password = TestAccounts.Password }, Cancel);
    var token = (await login.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("accessToken").GetString();

    using var disabled = ServerHost.InMemory(database);
    using var client = TestAccounts.Client(disabled);
    using var me = new HttpRequestMessage(HttpMethod.Get, Routes.CurrentUser);
    me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(me, Cancel)).StatusCode);
  }

  [Fact]
  public async Task DashboardConnection_MarksTheUserOnlineUntilItCloses()
  {
    using var server = ServerHost.InMemory();
    await TestAccounts.CreateUserAsync(server, "joana@example.com");
    var cookie = await TestAccounts.SignInForCookieAsync(server, "joana@example.com");

    var dashboard = await TestDashboard.ConnectAsync(server, cookie);
    var online = await Eventually.TrueAsync(() => server.WithDbAsync(db => db.Users.AnyAsync(x => x.IsOnline)));
    await dashboard.DisposeAsync();
    var offline = await Eventually.TrueAsync(() => server.WithDbAsync(db => db.Users.AnyAsync(x => !x.IsOnline)));

    Assert.True(online, "The user never showed as online.");
    Assert.True(offline, "The user stayed online after closing the dashboard.");
  }

  [Fact]
  public void PasswordAndAccountRules_FollowTheSettings()
  {
    using var server = ServerHost.InMemory().With("Accounts:RequireUniqueEmail", "false");

    var identity = server.Services.GetRequiredService<IOptions<IdentityOptions>>().Value;

    Assert.False(identity.User.RequireUniqueEmail);
    Assert.Equal(8, identity.Password.RequiredLength);
    Assert.True(identity.Lockout.AllowedForNewUsers);
  }

  [Fact]
  public async Task SignedInPrincipal_CarriesTheUserAndTenant()
  {
    using var server = ServerHost.InMemory();
    var user = await TestAccounts.CreateUserAsync(server, "kaio@example.com");
    await using var scope = server.Services.CreateAsyncScope();
    var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<UserRecord>>();

    var principal = await factory.CreateAsync(user);

    Assert.Equal(user.TenantId, principal.GetTenantId());
    Assert.Equal(user.Id.ToString(), principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);
  }
}
