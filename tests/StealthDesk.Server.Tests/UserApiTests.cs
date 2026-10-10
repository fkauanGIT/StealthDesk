using System.Net;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.Users;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Permissions;
using StealthDesk.Web.Server.Users;

namespace StealthDesk.Server.Tests;

/// <summary>A tenant's users through the API: listing, creating with presets, resetting passwords and deleting.</summary>
public class UserApiTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task List_ShowsTheTenantsUsersAndWhatTheyHold_AndNoOtherTenant()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (first, firstTenant) = await RegisterAsync(server, "first@example.com");
    var (second, secondTenant) = await RegisterAsync(server, "second@example.com");

    var list = await ListAsync(second, secondTenant);
    var other = await second.GetAsync(Users(firstTenant), Cancel);
    var ownByDefault = await second.GetAsync(Routes.Users, Cancel);

    var only = Assert.Single(list.Items);
    Assert.Equal("second@example.com", only.Email);
    Assert.Contains(PermissionNames.TenantUsersRead, only.Permissions);
    Assert.Equal(only.Permissions.Order(), only.Permissions);
    Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    Assert.Equal(HttpStatusCode.OK, ownByDefault.StatusCode);
    Assert.Single((await ListAsync(first, firstTenant)).Items);
  }

  [Fact]
  public async Task Create_ConfirmsTheUser_GrantsBaselineAndPresets_AndLogsTheGrant()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var response = await admin.PostAsJsonAsync(Users(tenant), new CreateUserRequest("tech@example.com", Password: TestAccounts.Password, Presets: [PermissionPresets.DeviceSuperuser]), Cancel);

    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    var created = (await response.Content.ReadFromJsonAsync<TenantUser>(Cancel))!;
    var expected = PermissionPresets.Baseline.Append(PermissionPresets.DeviceSuperuser).SelectMany(PermissionPresets.PermissionsOf).Distinct().Order();
    Assert.Equal(expected, created.Permissions);
    var stored = await server.WithDbAsync(db => db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == created.Id));
    Assert.True(stored.EmailConfirmed);
    Assert.Equal(tenant, stored.TenantId);
    Assert.Equal(1, await server.WithDbAsync(db => db.AuthorizationChanges.CountAsync(x => x.TargetId == created.Id && x.Action == AuthorizationChangeActions.PermissionAssignmentsSeeded)));
    Assert.False(string.IsNullOrEmpty(await TestAccounts.SignInForCookieAsync(server, "tech@example.com")));
  }

  [Fact]
  public async Task Create_WithoutAPassword_MakesAUserWhoCantSignInWithOne()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var response = await admin.PostAsJsonAsync(Users(tenant), new CreateUserRequest("nopass@example.com"), Cancel);

    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.Users.AnyAsync(x => x.Email == "nopass@example.com" && x.PasswordHash != null)));
  }

  [Theory]
  [InlineData(PermissionPresets.DeviceSuperuser, false, false, HttpStatusCode.Forbidden)]
  [InlineData(PermissionPresets.DeviceSuperuser, true, false, HttpStatusCode.Created)]
  [InlineData(PermissionPresets.TenantAdministrator, true, false, HttpStatusCode.Forbidden)]
  [InlineData(PermissionPresets.TenantAdministrator, true, true, HttpStatusCode.Created)]
  [InlineData(PermissionPresets.ServerAdministrator, true, true, HttpStatusCode.Forbidden)]
  // Its permissions live in the tenant too; the baseline every new user gets is the server's own grant.
  [InlineData(PermissionPresets.SelfService, false, false, HttpStatusCode.Forbidden)]
  public async Task Create_GivesAPresetOnlyToThoseWhoMayGrantWhatItHolds(string preset, bool managesTenant, bool deniesInTenant, HttpStatusCode expected)
  {
    using var server = ServerHost.InMemory();
    var (_, tenant) = await RegisterAsync(server, "admin@example.com");
    var manager = await TestAccounts.CreateUserAsync(server, "manager@example.com", tenant);
    await TestPermissions.AssignAsync(server, manager, PermissionNames.TenantUsersWrite, PermissionScopeKind.Tenant, tenant);
    if (managesTenant)
    {
      await TestPermissions.AssignAsync(server, manager, PermissionNames.TenantPermissionsWrite, PermissionScopeKind.Tenant, tenant);
    }

    if (deniesInTenant)
    {
      await TestPermissions.AssignAsync(server, manager, PermissionNames.TenantPermissionsDeny, PermissionScopeKind.Tenant, tenant);
    }

    using var client = await ClientForAsync(server, "manager@example.com");
    var response = await client.PostAsJsonAsync(Users(tenant), new CreateUserRequest("new@example.com", Presets: [preset]), Cancel);

    Assert.Equal(expected, response.StatusCode);
    Assert.Equal(expected == HttpStatusCode.Created, await server.WithDbAsync(db => db.Users.AnyAsync(x => x.Email == "new@example.com")));
  }

  [Fact]
  public async Task Create_TheServerAdministratorPreset_NeedsTheServersPermissionManager()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var response = await admin.PostAsJsonAsync(Users(tenant), new CreateUserRequest("deputy@example.com", Presets: [PermissionPresets.ServerAdministrator]), Cancel);
    var me = await MeAsync(await ClientForAsync(server, "admin@example.com"));

    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    Assert.True(me.IsServerAdministrator);
    var deputy = (await response.Content.ReadFromJsonAsync<TenantUser>(Cancel))!;
    Assert.Contains(PermissionNames.ServerPermissionsWrite, deputy.Permissions);
  }

  [Theory]
  [InlineData("{\"userName\":\"x@example.com\",\"presets\":[\"Root\"]}", "Presets not found: Root.")]
  [InlineData("{\"userName\":\"x@example.com\",\"password\":\"short\"}", "PasswordTooShort")]
  [InlineData("{\"userName\":\"admin@example.com\"}", "DuplicateUserName")]
  public async Task Create_RefusesBadRequests(string body, string reason)
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var response = await admin.PostAsync(Users(tenant), new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Contains(reason, await response.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(1, await server.WithDbAsync(db => db.Users.CountAsync()));
  }

  [Fact]
  public async Task ResetPassword_GivesATemporaryPassword_ThatMustBeChangedAtTheNextSignIn()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant, confirmed: true);

    var response = await admin.PostAsync(Routes.UserPasswordReset(tech.Id) + $"?tenantId={tenant}", null, Cancel);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var temporary = (await response.Content.ReadFromJsonAsync<TemporaryPassword>(Cancel))!.Password;
    using var anonymous = TestAccounts.Client(server);
    Assert.Equal(HttpStatusCode.Unauthorized, (await TestAccounts.SignInAsync(anonymous, "tech@example.com")).StatusCode);
    var signIn = await TestAccounts.SignInAsync(anonymous, "tech@example.com", temporary);
    Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
    using var techClient = TestAccounts.Client(server);
    techClient.DefaultRequestHeaders.Add("Cookie", TestAccounts.SessionCookie(signIn)!);
    Assert.True((await MeAsync(techClient)).MustChangePassword);
  }

  [Fact]
  public async Task ResetPassword_OfAnotherTenantsUser_IsNotFound()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (first, _) = await RegisterAsync(server, "first@example.com");
    var (_, secondTenant) = await RegisterAsync(server, "second@example.com");
    var outsider = await server.WithDbAsync(db => db.Users.IgnoreQueryFilters().SingleAsync(x => x.TenantId == secondTenant));
    var firstTenant = (await MeAsync(first)).TenantId;

    var response = await first.PostAsync(Routes.UserPasswordReset(outsider.Id) + $"?tenantId={firstTenant}", null, Cancel);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task Delete_RemovesTheUser_ButNeverYourself()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var me = await MeAsync(admin);
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant, confirmed: true);

    var self = await admin.DeleteAsync(Routes.User(me.Id) + $"?tenantId={tenant}", Cancel);
    var other = await admin.DeleteAsync(Routes.User(tech.Id) + $"?tenantId={tenant}", Cancel);
    var again = await admin.DeleteAsync(Routes.User(tech.Id) + $"?tenantId={tenant}", Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.Users.AnyAsync(x => x.Id == tech.Id)));
    using var anonymous = TestAccounts.Client(server);
    Assert.Equal(HttpStatusCode.Unauthorized, (await TestAccounts.SignInAsync(anonymous, "tech@example.com")).StatusCode);
  }

  [Fact]
  public async Task WithoutThePermissions_EveryRouteIsForbidden()
  {
    using var server = ServerHost.InMemory();
    var (_, tenant) = await RegisterAsync(server, "admin@example.com");
    var target = await TestAccounts.CreateUserAsync(server, "target@example.com", tenant);
    await TestAccounts.CreateUserAsync(server, "plain@example.com", tenant);
    using var plain = await ClientForAsync(server, "plain@example.com");

    Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync(Users(tenant), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsJsonAsync(Users(tenant), new CreateUserRequest("x@example.com"), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync(Routes.UserPasswordReset(target.Id) + $"?tenantId={tenant}", null, Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await plain.DeleteAsync(Routes.User(target.Id) + $"?tenantId={tenant}", Cancel)).StatusCode);
  }

  [Theory]
  [InlineData(8, true, true, true, false, 1)]
  [InlineData(12, true, true, true, true, 6)]
  [InlineData(4, false, false, false, false, 1)]
  [InlineData(20, false, true, false, true, 10)]
  public async Task TemporaryPasswords_MeetTheServersRules(int length, bool upper, bool lower, bool digit, bool symbol, int unique)
  {
    var rules = new PasswordOptions
    {
      RequiredLength = length,
      RequireUppercase = upper,
      RequireLowercase = lower,
      RequireDigit = digit,
      RequireNonAlphanumeric = symbol,
      RequiredUniqueChars = unique,
    };
    var validator = new PasswordValidator<UserRecord>();
    var manager = new UserManager<UserRecord>(
      new NoStore(), Microsoft.Extensions.Options.Options.Create(new IdentityOptions { Password = rules }), null!, [], [], null!, null!, null!, null!);

    for (var i = 0; i < 200; i++)
    {
      var password = TemporaryPasswords.Generate(rules);
      var result = await validator.ValidateAsync(manager, new UserRecord(), password);
      Assert.True(result.Succeeded, $"'{password}': {string.Join(", ", result.Errors.Select(x => x.Code))}");
    }
  }

  [Theory]
  [InlineData(true, "own", "own")]
  [InlineData(true, "empty", "own")]
  [InlineData(true, "other", null)]
  [InlineData(false, "other", "other")]
  [InlineData(false, "empty", null)]
  public void TenantFor_KeepsATenantsPrincipalInItsTenant(bool inTenant, string requested, string? expected)
  {
    var own = Guid.NewGuid();
    var other = Guid.NewGuid();
    var principal = new Principal(PermissionPrincipalKind.ServiceAccount, Guid.NewGuid(), inTenant ? own : null);
    Guid? Pick(string? name) => name switch { "own" => own, "other" => other, "empty" => Guid.Empty, _ => null };

    Assert.Equal(Pick(expected), principal.TenantFor(Pick(requested)!.Value));
  }

  private static string Users(Guid tenantId) => $"{Routes.Users}?tenantId={tenantId}";

  private static async Task<TenantUserList> ListAsync(HttpClient client, Guid tenant)
  {
    var response = await client.GetAsync(Users(tenant), Cancel);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<TenantUserList>(Cancel))!;
  }

  private static async Task<(HttpClient Client, Guid TenantId)> RegisterAsync(ServerHost server, string email)
  {
    using var anonymous = TestAccounts.Client(server);
    var response = await anonymous.PostAsJsonAsync($"{Routes.Auth}/register", new { email, password = TestAccounts.Password }, Cancel);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var client = await ClientForAsync(server, email);
    return (client, (await MeAsync(client)).TenantId);
  }

  private static async Task<HttpClient> ClientForAsync(ServerHost server, string email)
  {
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
    return client;
  }

  private static async Task<CurrentUser> MeAsync(HttpClient client) =>
    (await client.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel))!;

  // The password validator only needs a user manager to read the options from.
  private sealed class NoStore : IUserStore<UserRecord>
  {
    public void Dispose() { }
    public Task<string> GetUserIdAsync(UserRecord user, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string?> GetUserNameAsync(UserRecord user, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SetUserNameAsync(UserRecord user, string? userName, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string?> GetNormalizedUserNameAsync(UserRecord user, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SetNormalizedUserNameAsync(UserRecord user, string? normalizedName, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IdentityResult> CreateAsync(UserRecord user, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IdentityResult> UpdateAsync(UserRecord user, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IdentityResult> DeleteAsync(UserRecord user, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<UserRecord?> FindByIdAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<UserRecord?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw new NotSupportedException();
  }
}
