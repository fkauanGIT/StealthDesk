using System.Net;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.UserGroups;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>
/// Managing permission assignments: granting, changing and revoking (each logged), who may manage which scope and
/// effect, which scopes make sense, nobody locking themselves out, presets, and where an effective permission comes from.
/// </summary>
public class PermissionAssignmentApiTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task Granting_Changing_AndRevoking_EachLeaveOneEntryWithBeforeAndAfter()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);

    var granted = await CreateAsync(admin, tenant, Grant(tech.Id, PermissionNames.TenantUsersRead));
    var changed = await admin.PutAsJsonAsync(One(granted.Id, tenant), new UpdatePermissionAssignmentRequest(PermissionNames.TenantUsersRead, PermissionEffect.Allow, PermissionScopeKind.Tenant, IsEnabled: false), Cancel);
    var revoked = await admin.DeleteAsync(One(granted.Id, tenant), Cancel);

    Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
    var log = await server.WithDbAsync(db => db.AuthorizationChanges.Where(x => x.TargetId == granted.Id).OrderBy(x => x.CreatedAt).ToListAsync());
    Assert.Equal([AuthorizationChangeActions.PermissionAssignmentCreated, AuthorizationChangeActions.PermissionAssignmentUpdated, AuthorizationChangeActions.PermissionAssignmentDeleted], log.Select(x => x.Action));
    Assert.Null(log[0].BeforeJson);
    Assert.Contains("\"isEnabled\":true", log[1].BeforeJson);
    Assert.Contains("\"isEnabled\":false", log[1].AfterJson);
    Assert.Contains("\"permission\":\"tenant.users.read\"", log[2].BeforeJson);
    Assert.Null(log[2].AfterJson);
    Assert.All(log, x => Assert.Equal(AuthorizationChangeActors.User, x.ActorKind));
  }

  [Fact]
  public async Task AGroupsGrant_ReachesItsMember_AndTheCheckSaysItCameFromTheGroup()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    var group = await (await admin.PostAsJsonAsync($"{Routes.UserGroups}?tenantId={tenant}", new UserGroupRequest("Support"), Cancel)).Content.ReadFromJsonAsync<UserGroupDetail>(Cancel);
    await admin.PostAsJsonAsync($"{Routes.UserGroupMembers(group!.Id)}?tenantId={tenant}", new UserGroupMembersRequest([tech.Id]), Cancel);

    await CreateAsync(admin, tenant, new CreatePermissionAssignmentRequest(PermissionPrincipalKind.UserGroup, group.Id, PermissionNames.TenantUsersRead, PermissionEffect.Allow, PermissionScopeKind.Tenant));
    var forMember = await CheckAsync(admin, tenant, tech.Id, PermissionPrincipalKind.User, PermissionNames.TenantUsersRead, PermissionScopeKind.Tenant);
    var notGranted = await CheckAsync(admin, tenant, tech.Id, PermissionPrincipalKind.User, PermissionNames.TenantUsersWrite, PermissionScopeKind.Tenant);

    Assert.True(forMember.IsAllowed);
    Assert.Equal("Allowed by a user group assignment at Tenant scope.", forMember.Reason);
    Assert.False(notGranted.IsAllowed);
    Assert.Equal("No assignment grants it.", notGranted.Reason);
  }

  [Fact]
  public async Task ADirectDeny_BeatsTheGroupsAllow_AndTheCheckSaysSo()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    await CreateAsync(admin, tenant, Grant(tech.Id, PermissionNames.TenantUsersRead));

    await CreateAsync(admin, tenant, Grant(tech.Id, PermissionNames.TenantUsersRead) with { Effect = PermissionEffect.Deny });
    var check = await CheckAsync(admin, tenant, tech.Id, PermissionPrincipalKind.User, PermissionNames.TenantUsersRead, PermissionScopeKind.Tenant);

    Assert.False(check.IsAllowed);
    Assert.Equal("Denied by a direct assignment at Tenant scope.", check.Reason);
  }

  [Theory]
  [InlineData(PermissionNames.TenantUsersRead, PermissionEffect.Allow, PermissionScopeKind.Tenant, false, false, HttpStatusCode.Created)]
  [InlineData(PermissionNames.TenantUsersRead, PermissionEffect.Deny, PermissionScopeKind.Tenant, false, false, HttpStatusCode.Forbidden)]
  [InlineData(PermissionNames.TenantUsersRead, PermissionEffect.Deny, PermissionScopeKind.Tenant, true, false, HttpStatusCode.Created)]
  [InlineData(PermissionNames.ServerTenantsRead, PermissionEffect.Allow, PermissionScopeKind.Server, false, false, HttpStatusCode.Forbidden)]
  [InlineData(PermissionNames.ServerTenantsRead, PermissionEffect.Allow, PermissionScopeKind.Server, false, true, HttpStatusCode.Created)]
  public async Task Managing_NeedsTheRightForTheScopeAndEffect(string permission, PermissionEffect effect, PermissionScopeKind scope, bool deny, bool manageServer, HttpStatusCode expected)
  {
    using var server = ServerHost.InMemory();
    var (_, tenant) = await RegisterAsync(server, "admin@example.com");
    var manager = await TestAccounts.CreateUserAsync(server, "manager@example.com", tenant);
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    await TestPermissions.AssignAsync(server, manager, PermissionNames.TenantPermissionsWrite, PermissionScopeKind.Tenant, tenant);
    if (deny)
    {
      await TestPermissions.AssignAsync(server, manager, PermissionNames.TenantPermissionsDeny, PermissionScopeKind.Tenant, tenant);
    }

    if (manageServer)
    {
      await TestPermissions.AssignAsync(server, manager, PermissionNames.ServerPermissionsWrite, PermissionScopeKind.Server);
    }

    using var client = await ClientForAsync(server, "manager@example.com");
    var response = await client.PostAsJsonAsync(All(tenant), new CreatePermissionAssignmentRequest(PermissionPrincipalKind.User, tech.Id, permission, effect, scope), Cancel);

    Assert.Equal(expected, response.StatusCode);
  }

  [Fact]
  public async Task WithoutManagingPermissions_NothingCanBeChanged()
  {
    using var server = ServerHost.InMemory();
    var (_, tenant) = await RegisterAsync(server, "admin@example.com");
    var reader = await TestAccounts.CreateUserAsync(server, "reader@example.com", tenant);
    await TestPermissions.AssignAsync(server, reader, PermissionNames.TenantPermissionsRead, PermissionScopeKind.Tenant, tenant);
    using var client = await ClientForAsync(server, "reader@example.com");

    var list = await client.GetAsync($"{All(tenant)}&principalKind=User&principalId={reader.Id}", Cancel);
    var create = await client.PostAsJsonAsync(All(tenant), Grant(reader.Id, PermissionNames.TenantUsersRead), Cancel);

    Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
  }

  [Fact]
  public async Task Scopes_MustMakeSenseForThePermissionAndBelongToTheTenant()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var (other, otherTenant) = await RegisterAsync(server, "other@example.com");
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    var otherDevice = await DeviceInAsync(server, otherTenant);
    var otherGroup = await (await other.PostAsJsonAsync($"{Routes.UserGroups}?tenantId={otherTenant}", new UserGroupRequest("Theirs"), Cancel)).Content.ReadFromJsonAsync<UserGroupDetail>(Cancel);

    async Task<HttpResponseMessage> Try(string permission, PermissionScopeKind scope, Guid? scopeId = null, PermissionEffect effect = PermissionEffect.Allow, string? notes = null) =>
      await admin.PostAsJsonAsync(All(tenant), new CreatePermissionAssignmentRequest(PermissionPrincipalKind.User, tech.Id, permission, effect, scope, scopeId, notes), Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, (await Try("device.teleport", PermissionScopeKind.Tenant)).StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, (await Try(PermissionNames.TenantUsersRead, PermissionScopeKind.Device, Guid.NewGuid())).StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, (await Try(PermissionNames.DeviceRead, PermissionScopeKind.Device)).StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, (await Try(PermissionNames.DeviceRead, PermissionScopeKind.Device, otherDevice)).StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, (await Try(PermissionNames.UserGroupAssignUsers, PermissionScopeKind.UserGroup, otherGroup!.Id)).StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, (await Try(PermissionNames.DeviceRead, PermissionScopeKind.Server)).StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, (await Try(PermissionNames.TenantUsersRead, PermissionScopeKind.Tenant, notes: new string('n', 501))).StatusCode);
    // A server-wide deny of a tenant permission is fine: it only takes access away.
    Assert.Equal(HttpStatusCode.Created, (await Try(PermissionNames.DeviceRead, PermissionScopeKind.Server, effect: PermissionEffect.Deny)).StatusCode);
    Assert.Equal(HttpStatusCode.Created, (await Try(PermissionNames.DeviceRead, PermissionScopeKind.Device, await DeviceInAsync(server, tenant))).StatusCode);
  }

  [Fact]
  public async Task ThePrincipal_MustBeOfTheTenant()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    await RegisterAsync(server, "other@example.com");
    var outsider = await server.WithDbAsync(db => db.Users.IgnoreQueryFilters().SingleAsync(x => x.Email == "other@example.com"));

    var create = await admin.PostAsJsonAsync(All(tenant), Grant(outsider.Id, PermissionNames.TenantUsersRead), Cancel);
    var check = await admin.GetAsync(Check(tenant, outsider.Id, PermissionPrincipalKind.User, PermissionNames.TenantUsersRead, PermissionScopeKind.Tenant), Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, check.StatusCode);
  }

  [Fact]
  public async Task AnIdenticalAssignment_IsRefused()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    await CreateAsync(admin, tenant, Grant(tech.Id, PermissionNames.TenantUsersRead));

    var again = await admin.PostAsJsonAsync(All(tenant), Grant(tech.Id, PermissionNames.TenantUsersRead), Cancel);
    var simultaneous = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
      admin.PostAsJsonAsync(All(tenant), Grant(tech.Id, PermissionNames.TenantUsersWrite), Cancel)));

    Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    Assert.Single(simultaneous, x => x.StatusCode == HttpStatusCode.Created);
    Assert.All(simultaneous.Where(x => x.StatusCode != HttpStatusCode.Created), x => Assert.Equal(HttpStatusCode.Conflict, x.StatusCode));
  }

  // The check before saving can't stop two identical grants saved at the same instant; the database can.
  [Theory]
  [InlineData(PermissionScopeKind.Tenant)]
  [InlineData(PermissionScopeKind.Server)]
  public async Task TheDatabase_RefusesASecondIdenticalAssignment(PermissionScopeKind scope)
  {
    using var server = await ServerHost.OnPostgresAsync();
    var tenant = await TestTenants.CreateAsync(server);
    var principal = Guid.NewGuid();

    PermissionAssignmentRecord Row() => new()
    {
      PrincipalKind = PermissionPrincipalKind.User,
      PrincipalId = principal,
      Permission = PermissionNames.TenantUsersRead,
      Effect = PermissionEffect.Deny,
      ScopeKind = scope,
      ScopeId = scope == PermissionScopeKind.Server ? null : tenant,
      OwningTenantId = scope == PermissionScopeKind.Server ? null : tenant,
    };

    await server.WithDbAsync(db => { db.PermissionAssignments.Add(Row()); return db.SaveChangesAsync(); });

    await Assert.ThrowsAsync<DbUpdateException>(() => server.WithDbAsync(db => { db.PermissionAssignments.Add(Row()); return db.SaveChangesAsync(); }));
  }

  [Fact]
  public async Task NobodyRemovesFromThemselves_TheRightToManagePermissions()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var me = await MeAsync(admin);
    var own = await ListAsync(admin, tenant, PermissionPrincipalKind.User, me.Id);
    var write = own.Single(x => x.Permission == PermissionNames.TenantPermissionsWrite);
    var deviceRead = own.Single(x => x.Permission == PermissionNames.DeviceRead);

    var delete = await admin.DeleteAsync(One(write.Id, tenant), Cancel);
    var disable = await admin.PutAsJsonAsync(One(write.Id, tenant), new UpdatePermissionAssignmentRequest(write.Permission, write.Effect, write.ScopeKind, IsEnabled: false), Cancel);
    var deny = await admin.PostAsJsonAsync(All(tenant), Grant(me.Id, PermissionNames.TenantPermissionsDeny) with { Effect = PermissionEffect.Deny }, Cancel);
    var batch = await admin.PostAsJsonAsync($"{Routes.PermissionAssignmentsBatchDelete}?tenantId={tenant}", new DeletePermissionAssignmentsRequest([deviceRead.Id, write.Id]), Cancel);
    var replace = await admin.PostAsJsonAsync($"{Routes.ApplyPermissionPresets}?tenantId={tenant}", new ApplyPermissionPresetsRequest(PermissionPrincipalKind.User, me.Id, [PermissionPresets.SelfService], ReplaceExisting: true), Cancel);
    var unrelated = await admin.DeleteAsync(One(deviceRead.Id, tenant), Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
    Assert.Contains("You can't remove 'Manage permissions' from yourself", await delete.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(HttpStatusCode.BadRequest, disable.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, deny.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, batch.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, replace.StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, unrelated.StatusCode);
    Assert.True((await MeAsync(admin)).IsTenantAdministrator);
  }

  [Fact]
  public async Task Presets_MergeAddsWhatIsMissing_ReplaceSwapsTheTenantScopeOnes()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    var device = await DeviceInAsync(server, tenant);
    await CreateAsync(admin, tenant, Grant(tech.Id, PermissionNames.TenantUsersRead));
    await CreateAsync(admin, tenant, new CreatePermissionAssignmentRequest(PermissionPrincipalKind.User, tech.Id, PermissionNames.DeviceRead, PermissionEffect.Allow, PermissionScopeKind.Device, device));

    var merged = await ApplyAsync(admin, tenant, tech.Id, PermissionPresets.SelfService, replace: false);
    var mergedAgain = await ApplyAsync(admin, tenant, tech.Id, PermissionPresets.SelfService, replace: false);
    var afterMerge = await ListAsync(admin, tenant, PermissionPrincipalKind.User, tech.Id);
    var replaced = await ApplyAsync(admin, tenant, tech.Id, PermissionPresets.SelfService, replace: true);
    var afterReplace = await ListAsync(admin, tenant, PermissionPrincipalKind.User, tech.Id);

    var selfService = PermissionPresets.PermissionsOf(PermissionPresets.SelfService);
    Assert.Equal(selfService.Count, merged);
    Assert.Equal(0, mergedAgain);
    Assert.Contains(afterMerge, x => x.Permission == PermissionNames.TenantUsersRead);
    // Replacing removes the tenant-scope ones and grants every permission of the preset again.
    Assert.Equal(selfService.Count, replaced);
    Assert.DoesNotContain(afterReplace, x => x.Permission == PermissionNames.TenantUsersRead);
    Assert.Contains(afterReplace, x => x.ScopeKind == PermissionScopeKind.Device && x.ScopeId == device);
    Assert.Equal(selfService.Order(), afterReplace.Where(x => x.ScopeKind == PermissionScopeKind.Tenant).Select(x => x.Permission).Order());
  }

  [Fact]
  public async Task BatchDelete_RemovesWhatTheCallerCanSee_AndListsTheRest()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    var first = await CreateAsync(admin, tenant, Grant(tech.Id, PermissionNames.TenantUsersRead));
    var second = await CreateAsync(admin, tenant, Grant(tech.Id, PermissionNames.TenantUsersWrite));
    var missing = Guid.NewGuid();

    var response = await admin.PostAsJsonAsync($"{Routes.PermissionAssignmentsBatchDelete}?tenantId={tenant}", new DeletePermissionAssignmentsRequest([first.Id, second.Id, missing]), Cancel);

    var result = (await response.Content.ReadFromJsonAsync<DeletePermissionAssignmentsResult>(Cancel))!;
    Assert.Equal(new[] { first.Id, second.Id }.Order(), result.SuccessIds.Order());
    Assert.Equal([missing], result.FailureIds);
    Assert.Empty(await ListAsync(admin, tenant, PermissionPrincipalKind.User, tech.Id));
  }

  [Fact]
  public async Task AnotherTenantsAssignment_IsNotFound_AndServerRowsAreHiddenFromTenantAdministrators()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (first, firstTenant) = await RegisterAsync(server, "first@example.com");
    var (second, secondTenant) = await RegisterAsync(server, "second@example.com");
    var firstMe = await MeAsync(first);
    var firstRows = await ListAsync(first, firstTenant, PermissionPrincipalKind.User, firstMe.Id);
    var target = firstRows.First(x => x.ScopeKind == PermissionScopeKind.Tenant && x.Permission == PermissionNames.DeviceRead);

    var update = await second.PutAsJsonAsync(One(target.Id, secondTenant), new UpdatePermissionAssignmentRequest(target.Permission, target.Effect, target.ScopeKind), Cancel);
    var delete = await second.DeleteAsync(One(target.Id, secondTenant), Cancel);
    var secondMe = await MeAsync(second);
    var seenBySecond = await ListAsync(second, secondTenant, PermissionPrincipalKind.User, secondMe.Id);

    Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    Assert.Contains(firstRows, x => x.ScopeKind == PermissionScopeKind.Server);
    Assert.DoesNotContain(seenBySecond, x => x.ScopeKind == PermissionScopeKind.Server);
  }

  private static CreatePermissionAssignmentRequest Grant(Guid userId, string permission) =>
    new(PermissionPrincipalKind.User, userId, permission, PermissionEffect.Allow, PermissionScopeKind.Tenant);

  private static string All(Guid tenant) => $"{Routes.PermissionAssignments}?tenantId={tenant}";

  private static string One(Guid id, Guid tenant) => $"{Routes.PermissionAssignment(id)}?tenantId={tenant}";

  private static string Check(Guid tenant, Guid principalId, PermissionPrincipalKind kind, string permission, PermissionScopeKind scope) =>
    $"{Routes.EffectivePermission(principalId)}?tenantId={tenant}&principalKind={kind}&permission={permission}&scopeKind={scope}";

  private static async Task<PermissionAssignment> CreateAsync(HttpClient client, Guid tenant, CreatePermissionAssignmentRequest request)
  {
    var response = await client.PostAsJsonAsync(All(tenant), request, Cancel);
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<PermissionAssignment>(Cancel))!;
  }

  private static async Task<IReadOnlyList<PermissionAssignment>> ListAsync(HttpClient client, Guid tenant, PermissionPrincipalKind kind, Guid id) =>
    (await client.GetFromJsonAsync<PermissionAssignmentList>($"{All(tenant)}&principalKind={kind}&principalId={id}", Cancel))!.Items;

  private static async Task<EffectivePermission> CheckAsync(HttpClient client, Guid tenant, Guid principalId, PermissionPrincipalKind kind, string permission, PermissionScopeKind scope)
  {
    var response = await client.GetAsync(Check(tenant, principalId, kind, permission, scope), Cancel);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<EffectivePermission>(Cancel))!;
  }

  private static async Task<int> ApplyAsync(HttpClient client, Guid tenant, Guid userId, string preset, bool replace)
  {
    var response = await client.PostAsJsonAsync($"{Routes.ApplyPermissionPresets}?tenantId={tenant}", new ApplyPermissionPresetsRequest(PermissionPrincipalKind.User, userId, [preset], replace), Cancel);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return await response.Content.ReadFromJsonAsync<int>(Cancel);
  }

  private static Task<Guid> DeviceInAsync(ServerHost server, Guid tenant) => server.WithDbAsync(async db =>
  {
    var device = new DeviceRecord { Id = Guid.NewGuid(), TenantId = tenant, Name = "PC" };
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
