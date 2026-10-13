using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Time.Testing;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Invites;
using StealthDesk.Contracts.PersonalAccessTokens;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Permissions;
using StealthDesk.Web.Server.PersonalAccessTokens;

namespace StealthDesk.Server.Tests;

/// <summary>
/// Personal access tokens: a secret shown once and stored hashed, signing scripts in as their owner, never reaching
/// past their grants nor past their owner, expiring, throttled after failures, and managed by their owner or an
/// administrator of the tenant, each creation and removal logged.
/// </summary>
public class PersonalAccessTokenApiTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task Creating_ShowsTheSecretOnce_StoresItHashed_AndLogsTheTokenAndEachGrant()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var created = await CreateAsync(admin, Restricted("Backup script", Allow(PermissionNames.TenantUsersRead, tenant), Allow(PermissionNames.DeviceRead, tenant)));
    var listed = await admin.GetStringAsync(Routes.PersonalAccessTokens, Cancel);

    var secret = created.PlainTextToken.Split(':', 2);
    Assert.Equal(created.Token.Id.ToString(), secret[0]);
    Assert.Equal(64, secret[1].Length);
    Assert.DoesNotContain(secret[1], listed);
    var stored = await server.WithDbAsync(db => db.PersonalAccessTokens.SingleAsync(x => x.Id == created.Token.Id));
    Assert.DoesNotContain(secret[1], stored.HashedSecret);
    Assert.Equal(PersonalAccessTokenMode.Restricted, stored.Mode);
    Assert.Equal(2, created.Token.PermissionCount);
    Assert.Equal(CreatePersonalAccessTokenRequest.DefaultLifetimeDays, (stored.ExpiresAt!.Value - stored.CreatedAt).TotalDays, precision: 3);

    var log = await server.WithDbAsync(db => db.AuthorizationChanges.Where(x => x.OwningTenantId == tenant).OrderBy(x => x.CreatedAt).ToListAsync());
    var tokenEntry = Assert.Single(log, x => x.Action == AuthorizationChangeActions.PersonalAccessTokenCreated);
    Assert.Equal(created.Token.Id, tokenEntry.TargetId);
    Assert.Contains("\"name\":\"Backup script\"", tokenEntry.AfterJson);
    Assert.Contains("\"grants\":2", tokenEntry.AfterJson);
    var grants = await server.WithDbAsync(db => db.PermissionAssignments.Where(x => x.PrincipalKind == PermissionPrincipalKind.PersonalAccessToken && x.PrincipalId == created.Token.Id).Select(x => x.Id).ToListAsync());
    Assert.Equal(2, grants.Count);
    Assert.All(grants, id => Assert.Single(log, x => x.Action == AuthorizationChangeActions.PermissionAssignmentCreated && x.TargetId == id));
  }

  [Fact]
  public async Task ARestrictedToken_SignsInAsItsOwner_ButReachesOnlyItsGrants()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var created = await CreateAsync(admin, Restricted("Users only", Allow(PermissionNames.TenantUsersRead, tenant)));
    using var script = WithToken(server, created.PlainTextToken);

    var me = await script.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel);
    var users = await script.GetAsync($"{Routes.Users}?tenantId={tenant}", Cancel);
    var assignments = await script.GetAsync($"{Routes.PermissionAssignments}?tenantId={tenant}", Cancel);
    var tokens = await script.GetAsync(Routes.PersonalAccessTokens, Cancel);

    Assert.Equal("admin@example.com", me!.Email);
    Assert.Equal(HttpStatusCode.OK, users.StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, assignments.StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, tokens.StatusCode);
    var used = await server.WithDbAsync(db => db.PersonalAccessTokens.SingleAsync(x => x.Id == created.Token.Id));
    Assert.NotNull(used.LastUsed);
  }

  [Fact]
  public async Task WhatTheOwnerLoses_TheTokenLosesToo_DevicesIncluded()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var owner = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    await TestPermissions.AllowTenantDevicesAsync(server, owner);
    await TestPermissions.AssignAsync(server, owner, PermissionNames.TenantUsersRead, PermissionScopeKind.Tenant, tenant);
    await TestPermissions.AssignAsync(server, owner, PermissionNames.PersonalAccessTokenSelfWrite, PermissionScopeKind.Tenant, tenant);
    var kept = await DeviceInAsync(server, tenant, "Kept");
    var lost = await DeviceInAsync(server, tenant, "Lost");
    using var tech = await ClientForAsync(server, "tech@example.com");
    var created = await CreateAsync(tech, Restricted("Inventory", Allow(PermissionNames.DeviceRead, tenant), Allow(PermissionNames.TenantUsersRead, tenant)));
    using var script = WithToken(server, created.PlainTextToken);
    var before = await script.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, Cancel);

    await TestPermissions.AssignAsync(server, owner, PermissionNames.DeviceRead, PermissionScopeKind.Device, lost, PermissionEffect.Deny);
    await TestPermissions.AssignAsync(server, owner, PermissionNames.TenantUsersRead, PermissionScopeKind.Tenant, tenant, PermissionEffect.Deny);
    var after = await script.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, Cancel);
    var lostDevice = await script.GetAsync(Routes.Device(lost), Cancel);
    var users = await script.GetAsync($"{Routes.Users}?tenantId={tenant}", Cancel);

    Assert.Equal(new[] { kept, lost }.Order(), before!.Select(x => x.Id).Order());
    Assert.Equal([kept], after!.Select(x => x.Id));
    Assert.Equal(HttpStatusCode.NotFound, lostDevice.StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);
  }

  [Fact]
  public async Task AnInheritingToken_ActsAsItsOwner_ButNoTokenCanMintOne()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var inheriting = await CreateAsync(admin, new CreatePersonalAccessTokenRequest("Everything", PersonalAccessTokenMode.InheritOwner));
    using var script = WithToken(server, inheriting.PlainTextToken);

    var assignments = await script.GetAsync($"{Routes.PermissionAssignments}?tenantId={tenant}&principalKind=User&principalId={inheriting.Token.UserId}", Cancel);
    var mintInheriting = await script.PostAsJsonAsync(Routes.PersonalAccessTokens, new CreatePersonalAccessTokenRequest("Again", PersonalAccessTokenMode.InheritOwner), Cancel);
    var mintRestricted = await script.PostAsJsonAsync(Routes.PersonalAccessTokens, Restricted("Narrower", Allow(PermissionNames.TenantUsersRead, tenant)), Cancel);
    var withGrants = await admin.PostAsJsonAsync(Routes.PersonalAccessTokens, new CreatePersonalAccessTokenRequest("Both", PersonalAccessTokenMode.InheritOwner, Scopes: [Allow(PermissionNames.TenantUsersRead, tenant)]), Cancel);

    Assert.Equal(HttpStatusCode.OK, assignments.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, mintInheriting.StatusCode);
    Assert.Contains("signed-in session", await mintInheriting.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(HttpStatusCode.Created, mintRestricted.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, withGrants.StatusCode);
  }

  [Fact]
  public async Task Grants_MustBeWithinTheOwnersReach_AndMakeSense()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (_, tenant) = await RegisterAsync(server, "admin@example.com");
    var (_, otherTenant) = await RegisterAsync(server, "other@example.com");
    var owner = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    await TestPermissions.AssignAsync(server, owner, PermissionNames.PersonalAccessTokenSelfWrite, PermissionScopeKind.Tenant, tenant);
    var mine = await DeviceInAsync(server, tenant, "Mine");
    var theirs = await DeviceInAsync(server, otherTenant, "Theirs");
    await TestPermissions.AssignAsync(server, owner, PermissionNames.DeviceRead, PermissionScopeKind.Device, mine);
    using var tech = await ClientForAsync(server, "tech@example.com");

    async Task<HttpStatusCode> Try(params CredentialScope[] scopes) =>
      (await tech.PostAsJsonAsync(Routes.PersonalAccessTokens, Restricted("Try", scopes), Cancel)).StatusCode;

    Assert.Equal(HttpStatusCode.BadRequest, await Try(Allow(PermissionNames.TenantUsersRead, tenant)));
    Assert.Equal(HttpStatusCode.BadRequest, await Try(Allow(PermissionNames.DeviceRead, tenant)));
    Assert.Equal(HttpStatusCode.BadRequest, await Try(new CredentialScope(PermissionNames.DeviceRead, PermissionScopeKind.Device, theirs)));
    Assert.Equal(HttpStatusCode.BadRequest, await Try(new CredentialScope("device.teleport", PermissionScopeKind.Tenant, tenant)));
    Assert.Equal(HttpStatusCode.BadRequest, await Try(new CredentialScope(PermissionNames.DeviceRead, PermissionScopeKind.Server, null)));
    Assert.Equal(HttpStatusCode.Created, await Try(new CredentialScope(PermissionNames.DeviceRead, PermissionScopeKind.Device, mine)));
    Assert.Equal(HttpStatusCode.BadRequest, (await tech.PostAsJsonAsync(Routes.PersonalAccessTokens, new CreatePersonalAccessTokenRequest(" "), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, (await tech.PostAsJsonAsync(Routes.PersonalAccessTokens, new CreatePersonalAccessTokenRequest("Odd", ExpiresInDays: 7), Cancel)).StatusCode);
  }

  [Fact]
  public async Task AToken_StopsWorkingWhenItExpires_UnlessMadeToNeverExpire()
  {
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    using var server = ServerHost.InMemory().WithServices(services => services.AddSingleton<TimeProvider>(clock));
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var monthly = await CreateAsync(admin, Restricted("Monthly", Allow(PermissionNames.TenantUsersRead, tenant)) with { ExpiresInDays = 30 });
    var forever = await CreateAsync(admin, Restricted("Forever", Allow(PermissionNames.TenantUsersRead, tenant)) with { ExpiresInDays = null });
    using var monthlyScript = WithToken(server, monthly.PlainTextToken);
    using var foreverScript = WithToken(server, forever.PlainTextToken);
    var users = $"{Routes.Users}?tenantId={tenant}";

    clock.Advance(TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1));
    var lastMinute = await monthlyScript.GetAsync(users, Cancel);
    clock.Advance(TimeSpan.FromMinutes(1));
    var expired = await monthlyScript.GetAsync(users, Cancel);
    clock.Advance(TimeSpan.FromDays(3650));
    var tenYears = await foreverScript.GetAsync(users, Cancel);

    Assert.Equal(HttpStatusCode.OK, lastMinute.StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    Assert.Equal(HttpStatusCode.OK, tenYears.StatusCode);
    Assert.Null(forever.Token.ExpiresAt);
  }

  [Fact]
  public async Task RepeatedFailures_AreRefusedForAWhile_EvenWithTheRightSecret()
  {
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    using var server = ServerHost.InMemory().WithServices(services => services.AddSingleton<TimeProvider>(clock));
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var created = await CreateAsync(admin, Restricted("Script", Allow(PermissionNames.TenantUsersRead, tenant)));
    var users = $"{Routes.Users}?tenantId={tenant}";
    using var wrong = WithToken(server, $"{created.Token.Id}:{new string('x', 64)}");
    using var right = WithToken(server, created.PlainTextToken);

    var beforeLimit = await right.GetAsync(users, Cancel);
    for (var i = 0; i < PersonalAccessTokenAuthentication.MaxFailures; i++)
    {
      Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync(users, Cancel)).StatusCode);
    }

    var throttled = await right.GetAsync(users, Cancel);

    Assert.Equal(HttpStatusCode.OK, beforeLimit.StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, throttled.StatusCode);
  }

  [Theory]
  [InlineData("not-a-token")]
  [InlineData("00000000-0000-0000-0000-000000000000:secret")]
  [InlineData("{id}:")]
  [InlineData("{id}:{secret}x")]
  public async Task AnUnknownOrMalformedToken_IsUnauthorized_EvenWithASessionCookie(string presented)
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var created = await CreateAsync(admin, Restricted("Script", Allow(PermissionNames.TenantUsersRead, tenant)));
    var parts = created.PlainTextToken.Split(':', 2);
    using var client = await ClientForAsync(server, "admin@example.com");
    client.DefaultRequestHeaders.Add(PersonalAccessTokenHeader.Name, presented.Replace("{id}", parts[0]).Replace("{secret}", parts[1]));

    var response = await client.GetAsync($"{Routes.Users}?tenantId={tenant}", Cancel);

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task ALockedOutOwner_CantUseTheirTokens()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var created = await CreateAsync(admin, Restricted("Script", Allow(PermissionNames.TenantUsersRead, tenant)));
    using var script = WithToken(server, created.PlainTextToken);

    await using (var scope = server.Services.CreateAsyncScope())
    {
      var users = scope.ServiceProvider.GetRequiredService<UserManager<UserRecord>>();
      var owner = await users.FindByEmailAsync("admin@example.com");
      await users.SetLockoutEndDateAsync(owner!, DateTimeOffset.UtcNow.AddHours(1));
    }

    Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync($"{Routes.Users}?tenantId={tenant}", Cancel)).StatusCode);
  }

  [Fact]
  public async Task Renaming_AndDeleting_TheDeletedTokenStopsWorking_AndItsGrantsGoWithIt()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var created = await CreateAsync(admin, Restricted("Script", Allow(PermissionNames.TenantUsersRead, tenant)));
    using var script = WithToken(server, created.PlainTextToken);

    var renamed = await admin.PutAsJsonAsync(Routes.PersonalAccessToken(created.Token.Id), new RenamePersonalAccessTokenRequest(" Nightly backup "), Cancel);
    var deleted = await admin.DeleteAsync(Routes.PersonalAccessToken(created.Token.Id), Cancel);
    var deletedAgain = await admin.DeleteAsync(Routes.PersonalAccessToken(created.Token.Id), Cancel);
    var afterwards = await script.GetAsync($"{Routes.Users}?tenantId={tenant}", Cancel);

    Assert.Equal("Nightly backup", (await renamed.Content.ReadFromJsonAsync<PersonalAccessTokenSummary>(Cancel))!.Name);
    Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, deletedAgain.StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.PermissionAssignments.AnyAsync(x => x.PrincipalId == created.Token.Id)));
    var entry = await server.WithDbAsync(db => db.AuthorizationChanges.SingleAsync(x => x.Action == AuthorizationChangeActions.PersonalAccessTokenDeleted));
    Assert.Equal(created.Token.Id, entry.TargetId);
    Assert.Contains("\"name\":\"Nightly backup\"", entry.BeforeJson);
    Assert.Contains("\"grants\":1", entry.BeforeJson);
    Assert.Null(entry.AfterJson);
  }

  [Fact]
  public async Task AnAdministrator_ManagesAnotherUsersTokens_OthersOnlyTheirOwn()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var (outsiderAdmin, otherTenant) = await RegisterAsync(server, "other@example.com");
    var owner = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    await TestPermissions.AssignAsync(server, owner, PermissionNames.PersonalAccessTokenSelfRead, PermissionScopeKind.Tenant, tenant);
    await TestPermissions.AssignAsync(server, owner, PermissionNames.PersonalAccessTokenSelfWrite, PermissionScopeKind.Tenant, tenant);
    await TestPermissions.AssignAsync(server, owner, PermissionNames.TenantUsersRead, PermissionScopeKind.Tenant, tenant);
    using var tech = await ClientForAsync(server, "tech@example.com");
    var others = $"{Routes.UserPersonalAccessTokens(owner.Id)}?tenantId={tenant}";

    var forTech = await admin.PostAsJsonAsync(others, Restricted("Set up by IT", Allow(PermissionNames.TenantUsersRead, tenant)), Cancel);
    var techSees = await tech.GetFromJsonAsync<PersonalAccessTokenList>(Routes.PersonalAccessTokens, Cancel);
    var adminSees = await admin.GetFromJsonAsync<PersonalAccessTokenList>(others, Cancel);
    var techOnAdmin = await tech.GetAsync($"{Routes.UserPersonalAccessTokens((await MeAsync(admin)).Id)}?tenantId={tenant}", Cancel);
    var outsider = await outsiderAdmin.GetAsync($"{Routes.UserPersonalAccessTokens(owner.Id)}?tenantId={otherTenant}", Cancel);
    var beyondOwner = await admin.PostAsJsonAsync(others, Restricted("Too much", Allow(PermissionNames.TenantPermissionsWrite, tenant)), Cancel);
    var token = (await forTech.Content.ReadFromJsonAsync<CreatedPersonalAccessToken>(Cancel))!.Token;
    var removed = await admin.DeleteAsync($"{Routes.UserPersonalAccessToken(owner.Id, token.Id)}?tenantId={tenant}", Cancel);

    Assert.Equal(HttpStatusCode.Created, forTech.StatusCode);
    Assert.Equal(owner.Id, token.UserId);
    Assert.Equal([token.Id], techSees!.Items.Select(x => x.Id));
    Assert.Equal([token.Id], adminSees!.Items.Select(x => x.Id));
    Assert.Equal(HttpStatusCode.Forbidden, techOnAdmin.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, beyondOwner.StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
  }

  [Fact]
  public async Task NobodyTouchesAnotherUsersToken_ThroughTheirOwnRoutes()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var owner = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    await TestPermissions.AssignAsync(server, owner, PermissionNames.PersonalAccessTokenSelfWrite, PermissionScopeKind.Tenant, tenant);
    using var tech = await ClientForAsync(server, "tech@example.com");
    var adminsToken = await CreateAsync(admin, Restricted("Admin's", Allow(PermissionNames.TenantUsersRead, tenant)));

    var rename = await tech.PutAsJsonAsync(Routes.PersonalAccessToken(adminsToken.Token.Id), new RenamePersonalAccessTokenRequest("Mine now"), Cancel);
    var delete = await tech.DeleteAsync(Routes.PersonalAccessToken(adminsToken.Token.Id), Cancel);

    Assert.Equal(HttpStatusCode.NotFound, rename.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    Assert.True(await server.WithDbAsync(db => db.PersonalAccessTokens.AnyAsync(x => x.Id == adminsToken.Token.Id && x.Name == "Admin's")));
  }

  [Fact]
  public async Task GrantingToATokenLater_StaysWithinItsOwner_AndReachesTheScript()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var (outsiderAdmin, otherTenant) = await RegisterAsync(server, "other@example.com");
    var owner = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    await TestPermissions.AssignAsync(server, owner, PermissionNames.PersonalAccessTokenSelfWrite, PermissionScopeKind.Tenant, tenant);
    await TestPermissions.AssignAsync(server, owner, PermissionNames.TenantUsersRead, PermissionScopeKind.Tenant, tenant);
    using var tech = await ClientForAsync(server, "tech@example.com");
    var created = await CreateAsync(tech, Restricted("Empty for now"));
    using var script = WithToken(server, created.PlainTextToken);
    var users = $"{Routes.Users}?tenantId={tenant}";

    CreatePermissionAssignmentRequest ToToken(string permission) =>
      new(PermissionPrincipalKind.PersonalAccessToken, created.Token.Id, permission, PermissionEffect.Allow, PermissionScopeKind.Tenant);

    var beforeGrant = await script.GetAsync(users, Cancel);
    var beyond = await admin.PostAsJsonAsync($"{Routes.PermissionAssignments}?tenantId={tenant}", ToToken(PermissionNames.TenantPermissionsWrite), Cancel);
    var fromOutside = await outsiderAdmin.PostAsJsonAsync($"{Routes.PermissionAssignments}?tenantId={otherTenant}", ToToken(PermissionNames.TenantUsersRead), Cancel);
    var within = await admin.PostAsJsonAsync($"{Routes.PermissionAssignments}?tenantId={tenant}", ToToken(PermissionNames.TenantUsersRead), Cancel);
    var afterGrant = await script.GetAsync(users, Cancel);
    var effective = await admin.GetAsync($"{Routes.EffectivePermission(created.Token.Id)}?tenantId={tenant}&principalKind=PersonalAccessToken&permission={PermissionNames.TenantUsersRead}&scopeKind=Tenant", Cancel);

    Assert.Equal(HttpStatusCode.Forbidden, beforeGrant.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, beyond.StatusCode);
    Assert.Contains("beyond what the token's owner can do", await beyond.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(HttpStatusCode.BadRequest, fromOutside.StatusCode);
    Assert.Equal(HttpStatusCode.Created, within.StatusCode);
    Assert.Equal(HttpStatusCode.OK, afterGrant.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, effective.StatusCode);
  }

  // Signed in through a token that's gone by the time a permission is checked: it grants nothing, not its owner's all.
  [Fact]
  public async Task ATokenThatNoLongerExists_GrantsNothing()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var me = await MeAsync(admin);
    await using var scope = server.Services.CreateAsyncScope();
    var evaluator = scope.ServiceProvider.GetRequiredService<IPermissionEvaluator>();

    var asOwner = await evaluator.EvaluateAsync(new Principal(PermissionPrincipalKind.User, me.Id, tenant), PermissionNames.TenantUsersRead, Resource.Tenant(tenant), Cancel);
    var throughGoneToken = await evaluator.EvaluateAsync(new Principal(PermissionPrincipalKind.User, me.Id, tenant, Guid.NewGuid()), PermissionNames.TenantUsersRead, Resource.Tenant(tenant), Cancel);

    Assert.True(asOwner.Allowed);
    Assert.False(throughGoneToken.Allowed);
  }

  [Fact]
  public async Task AcceptingAnInvite_RemovesTheAccountsTokens_AndLogsIt()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var invite = (await (await admin.PostAsJsonAsync($"{Routes.Invites}?tenantId={tenant}", new CreateInviteRequest("joao@example.com"), Cancel)).Content.ReadFromJsonAsync<TenantInvite>(Cancel))!;
    var account = await server.WithDbAsync(db => db.Users.IgnoreQueryFilters().SingleAsync(x => x.Email == "joao@example.com"));
    var leftover = await CreateAsync(admin, Restricted("Leftover", Allow(PermissionNames.PersonalAccessTokenSelfRead, tenant)), Routes.UserPersonalAccessTokens(account.Id) + $"?tenantId={tenant}");

    using var anonymous = TestAccounts.Client(server);
    var accepted = await anonymous.PostAsJsonAsync(Routes.AcceptInvite, new AcceptInviteRequest(invite.InviteUrl[(invite.InviteUrl.LastIndexOf('/') + 1)..], "joao@example.com", "New-password-9"), Cancel);

    Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.PersonalAccessTokens.AnyAsync(x => x.UserId == account.Id)));
    Assert.False(await server.WithDbAsync(db => db.PermissionAssignments.IgnoreQueryFilters().AnyAsync(x => x.PrincipalId == leftover.Token.Id)));
    Assert.True(await server.WithDbAsync(db => db.AuthorizationChanges.AnyAsync(x => x.Action == AuthorizationChangeActions.PersonalAccessTokenDeleted && x.TargetId == leftover.Token.Id)));
  }

  private static CredentialScope Allow(string permission, Guid tenant) => new(permission, PermissionScopeKind.Tenant, tenant);

  private static CreatePersonalAccessTokenRequest Restricted(string name, params CredentialScope[] scopes) =>
    new(name, PersonalAccessTokenMode.Restricted, Scopes: scopes);

  private static HttpClient WithToken(ServerHost server, string token)
  {
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add(PersonalAccessTokenHeader.Name, token);
    return client;
  }

  private static async Task<CreatedPersonalAccessToken> CreateAsync(HttpClient client, CreatePersonalAccessTokenRequest request, string route = Routes.PersonalAccessTokens)
  {
    var response = await client.PostAsJsonAsync(route, request, Cancel);
    Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Cancel));
    return (await response.Content.ReadFromJsonAsync<CreatedPersonalAccessToken>(Cancel))!;
  }

  private static Task<Guid> DeviceInAsync(ServerHost server, Guid tenant, string name) => server.WithDbAsync(async db =>
  {
    var device = new DeviceRecord { Id = Guid.NewGuid(), TenantId = tenant, Name = name };
    db.Devices.Add(device);
    await db.SaveChangesAsync();
    return device.Id;
  });

  private static async Task<CurrentUser> MeAsync(HttpClient client) =>
    (await client.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel))!;

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
}
