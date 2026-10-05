using System.Net;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Accounts;

namespace StealthDesk.Server.Tests;

/// <summary>Who may register: the first user of a new server, then only anyone when public registration is on.</summary>
public class RegistrationTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task FirstRegistration_CreatesATenantAndTheServerAdministrator()
  {
    using var server = await ServerHost.OnPostgresAsync();
    using var client = TestAccounts.Client(server);

    var register = await RegisterAsync(client, "first@example.com");
    var cookie = await TestAccounts.SignInForCookieAsync(server, "first@example.com");
    var me = await Me(client, cookie);

    Assert.Equal(HttpStatusCode.OK, register.StatusCode);
    Assert.True(me.IsServerAdministrator);
    Assert.True(me.IsTenantAdministrator);
    Assert.True(me.EmailConfirmed);
    var tenant = Assert.Single(await server.WithDbAsync(db => db.Tenants.ToListAsync()));
    Assert.Equal(tenant.Id, me.TenantId);
  }

  [Fact]
  public async Task SecondRegistration_IsRefusedWhilePublicRegistrationIsOff()
  {
    using var server = ServerHost.InMemory();
    using var client = TestAccounts.Client(server);
    await RegisterAsync(client, "first@example.com");

    var second = await RegisterAsync(client, "second@example.com");

    Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    Assert.Equal(1, await server.WithDbAsync(db => db.Users.CountAsync()));
    Assert.Equal(1, await server.WithDbAsync(db => db.Tenants.CountAsync()));
  }

  [Fact]
  public async Task PublicRegistration_GivesEachUserTheirOwnTenant()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    using var client = TestAccounts.Client(server);
    await RegisterAsync(client, "first@example.com");

    var second = await RegisterAsync(client, "second@example.com");
    var me = await Me(client, await TestAccounts.SignInForCookieAsync(server, "second@example.com"));

    Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    Assert.Equal(2, await server.WithDbAsync(db => db.Tenants.CountAsync()));
    Assert.False(me.IsServerAdministrator);
    Assert.True(me.IsTenantAdministrator);
    Assert.False(me.EmailConfirmed);
  }

  [Fact]
  public async Task FirstUserRegistration_CanBeTurnedOff()
  {
    using var server = ServerHost.InMemory().With("Accounts:DisableFirstUserSelfRegistration", "true");
    using var client = TestAccounts.Client(server);

    var register = await RegisterAsync(client, "first@example.com");

    Assert.Equal(HttpStatusCode.NotFound, register.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.Users.AnyAsync()));
  }

  [Fact]
  public async Task FirstUserRegistrationOff_WithPublicRegistration_MakesNoServerAdministrator()
  {
    using var server = ServerHost.InMemory()
      .With("Accounts:DisableFirstUserSelfRegistration", "true")
      .With("Accounts:EnablePublicRegistration", "true");
    using var client = TestAccounts.Client(server);

    await RegisterAsync(client, "first@example.com");
    var me = await Me(client, await TestAccounts.SignInForCookieAsync(server, "first@example.com"));

    Assert.False(me.IsServerAdministrator);
    Assert.True(me.IsTenantAdministrator);
  }

  [Fact]
  public async Task SimultaneousFirstRegistrations_MakeExactlyOneUser()
  {
    using var server = await ServerHost.OnPostgresAsync();

    var attempts = await Task.WhenAll(Enumerable.Range(1, 6).Select(async i =>
    {
      using var client = TestAccounts.Client(server);
      return await RegisterAsync(client, $"racer{i}@example.com");
    }));

    Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.OK);
    Assert.Equal(1, await server.WithDbAsync(db => db.Users.CountAsync()));
    Assert.Equal(1, await server.WithDbAsync(db => db.Tenants.CountAsync()));
  }

  [Fact]
  public async Task SimultaneousPublicRegistrations_MakeOneServerAdministrator()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");

    await Task.WhenAll(Enumerable.Range(1, 6).Select(async i =>
    {
      using var client = TestAccounts.Client(server);
      return await RegisterAsync(client, $"racer{i}@example.com");
    }));

    var administrators = await server.WithDbAsync(db =>
      db.UserClaims.CountAsync(x => x.ClaimType == StealthDeskClaims.ServerAdministrator));
    Assert.Equal(6, await server.WithDbAsync(db => db.Users.CountAsync()));
    Assert.Equal(1, administrators);
  }

  [Fact]
  public async Task WeakPassword_IsRefusedAndLeavesNoTenantBehind()
  {
    using var server = ServerHost.InMemory();
    using var client = TestAccounts.Client(server);

    var register = await client.PostAsJsonAsync($"{Routes.Auth}/register", new { email = "weak@example.com", password = "short" }, Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, register.StatusCode);
    Assert.Contains("PasswordTooShort", await register.Content.ReadAsStringAsync(Cancel));
    Assert.False(await server.WithDbAsync(db => db.Tenants.AnyAsync()));
  }

  [Fact]
  public async Task Settings_SayWhetherRegistrationIsOpen()
  {
    using var server = ServerHost.InMemory();
    using var client = TestAccounts.Client(server);

    var before = await client.GetFromJsonAsync<AccountSettings>(Routes.AuthSettings, Cancel);
    await RegisterAsync(client, "first@example.com");
    var after = await client.GetFromJsonAsync<AccountSettings>(Routes.AuthSettings, Cancel);

    Assert.True(before!.RegistrationOpen);
    Assert.False(after!.RegistrationOpen);
  }

  [Fact]
  public async Task RegisteredAgent_JoinsTheFirstUsersTenant()
  {
    using var server = ServerHost.InMemory();
    using var client = TestAccounts.Client(server);
    await RegisterAsync(client, "first@example.com");
    var tenantId = await server.WithDbAsync(db => db.Tenants.Select(x => x.Id).SingleAsync());
    await using var agent = await TestAgent.ConnectAsync(server);

    var reply = await agent.ReportAsync(TestAgent.Report(Guid.NewGuid()));

    Assert.True(reply.Accepted, reply.Error);
    Assert.Equal(tenantId, reply.Value!.TenantId);
  }

  private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email) =>
    client.PostAsJsonAsync($"{Routes.Auth}/register", new { email, password = TestAccounts.Password }, Cancel);

  private static async Task<CurrentUser> Me(HttpClient client, string cookie)
  {
    var response = await client.SendAsync(TestAccounts.WithCookie(HttpMethod.Get, Routes.CurrentUser, cookie), Cancel);
    return (await response.Content.ReadFromJsonAsync<CurrentUser>(Cancel))!;
  }
}
