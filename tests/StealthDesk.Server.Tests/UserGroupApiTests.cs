using System.Net;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.UserGroups;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>User groups through the API: managing them, their members, what members inherit, and the log.</summary>
public class UserGroupApiTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task Group_IsCreatedRenamedAndDeleted_EachChangeLogged()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var created = await CreateAsync(admin, tenant, "Support", "First line");
    var renamed = await admin.PutAsJsonAsync(Group(created.Id, tenant), new UserGroupRequest(" Help desk ", "  "), Cancel);
    var list = await admin.GetFromJsonAsync<UserGroupList>(Groups(tenant), Cancel);
    var deleted = await admin.DeleteAsync(Group(created.Id, tenant), Cancel);
    var gone = await admin.GetAsync(Group(created.Id, tenant), Cancel);

    Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
    var detail = (await renamed.Content.ReadFromJsonAsync<UserGroupDetail>(Cancel))!;
    Assert.Equal("Help desk", detail.Name);
    Assert.Null(detail.Description);
    Assert.Equal("Help desk", Assert.Single(list!.Items).Name);
    Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

    var log = await LogAsync(server, created.Id);
    Assert.Equal([AuthorizationChangeActions.UserGroupCreated, AuthorizationChangeActions.UserGroupUpdated, AuthorizationChangeActions.UserGroupDeleted], log.Select(x => x.Action));
    Assert.Contains("\"name\":\"Support\"", log[1].BeforeJson);
    Assert.Contains("\"name\":\"Help desk\"", log[1].AfterJson);
    Assert.All(log, x => Assert.Equal(AuthorizationChangeActors.User, x.ActorKind));
  }

  [Theory]
  [InlineData("", null, HttpStatusCode.BadRequest)]
  [InlineData("   ", null, HttpStatusCode.BadRequest)]
  [InlineData("Support", null, HttpStatusCode.Conflict)]
  [InlineData(" Support ", null, HttpStatusCode.Conflict)]
  public async Task Create_RefusesAMissingOrTakenName(string name, string? description, HttpStatusCode expected)
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    await CreateAsync(admin, tenant, "Support");

    var response = await admin.PostAsJsonAsync(Groups(tenant), new UserGroupRequest(name, description), Cancel);

    Assert.Equal(expected, response.StatusCode);
    Assert.Equal(1, await server.WithDbAsync(db => db.UserGroups.CountAsync()));
  }

  [Fact]
  public async Task Create_RefusesTooLongNamesAndDescriptions()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var longName = await admin.PostAsJsonAsync(Groups(tenant), new UserGroupRequest(new string('n', 101)), Cancel);
    var longDescription = await admin.PostAsJsonAsync(Groups(tenant), new UserGroupRequest("Ok", new string('d', 501)), Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, longName.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, longDescription.StatusCode);
  }

  [Fact]
  public async Task Members_InheritTheGroupsGrants_UntilTheyLeave()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant, confirmed: true);
    using var techClient = await ClientForAsync(server, "tech@example.com");
    var group = await CreateAsync(admin, tenant, "Support");
    await GrantGroupAsync(server, group.Id, tenant, PermissionNames.TenantUsersRead);
    Assert.DoesNotContain(PermissionNames.TenantUsersRead, await HeldAsync(techClient));

    var added = await admin.PostAsJsonAsync(Members(group.Id, tenant), new UserGroupMembersRequest([tech.Id, tech.Id]), Cancel);

    Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
    Assert.Contains(PermissionNames.TenantUsersRead, await HeldAsync(techClient));
    Assert.Equal(HttpStatusCode.OK, (await techClient.GetAsync($"{Routes.Users}?tenantId={tenant}", Cancel)).StatusCode);
    var detail = await admin.GetFromJsonAsync<UserGroupDetail>(Group(group.Id, tenant), Cancel);
    Assert.Equal("tech@example.com", Assert.Single(detail!.Members).UserName);

    var removed = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, Members(group.Id, tenant))
    {
      Content = JsonContent.Create(new UserGroupMembersRequest([tech.Id])),
    }, Cancel);

    Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    Assert.DoesNotContain(PermissionNames.TenantUsersRead, await HeldAsync(techClient));
    Assert.Equal(HttpStatusCode.Forbidden, (await techClient.GetAsync($"{Routes.Users}?tenantId={tenant}", Cancel)).StatusCode);

    var log = await LogAsync(server, group.Id);
    Assert.Equal([AuthorizationChangeActions.UserGroupCreated, AuthorizationChangeActions.UserGroupMembersAdded, AuthorizationChangeActions.UserGroupMembersRemoved], log.Select(x => x.Action));
    Assert.Equal("{\"count\":1}", log[1].AfterJson);
  }

  [Fact]
  public async Task Members_AddingThoseAlreadyIn_ChangesAndLogsNothing()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    var group = await CreateAsync(admin, tenant, "Support");
    await admin.PostAsJsonAsync(Members(group.Id, tenant), new UserGroupMembersRequest([tech.Id]), Cancel);

    var again = await admin.PostAsJsonAsync(Members(group.Id, tenant), new UserGroupMembersRequest([tech.Id]), Cancel);

    Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    Assert.Single(await LogAsync(server, group.Id), x => x.Action == AuthorizationChangeActions.UserGroupMembersAdded);
  }

  [Fact]
  public async Task Members_FromAnotherTenant_AreRefused()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    await RegisterAsync(server, "outsider@example.com");
    var outsider = await server.WithDbAsync(db => db.Users.IgnoreQueryFilters().SingleAsync(x => x.Email == "outsider@example.com"));
    var group = await CreateAsync(admin, tenant, "Support");

    var response = await admin.PostAsJsonAsync(Members(group.Id, tenant), new UserGroupMembersRequest([outsider.Id]), Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.UserGroupMembers.AnyAsync()));
  }

  [Fact]
  public async Task Delete_TakesTheGroupsGrantsAndMembershipsWithIt()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    var group = await CreateAsync(admin, tenant, "Support");
    await GrantGroupAsync(server, group.Id, tenant, PermissionNames.DeviceRead);
    await admin.PostAsJsonAsync(Members(group.Id, tenant), new UserGroupMembersRequest([tech.Id]), Cancel);

    var deleted = await admin.DeleteAsync(Group(group.Id, tenant), Cancel);

    Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.PermissionAssignments.IgnoreQueryFilters().AnyAsync(x => x.PrincipalId == group.Id)));
    Assert.False(await server.WithDbAsync(db => db.UserGroupMembers.IgnoreQueryFilters().AnyAsync()));
    Assert.True(await server.WithDbAsync(db => db.Users.IgnoreQueryFilters().AnyAsync(x => x.Id == tech.Id)));
  }

  [Fact]
  public async Task AssignUsers_GrantedForOneGroup_ManagesOnlyThatGroup()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var support = await CreateAsync(admin, tenant, "Support");
    var finance = await CreateAsync(admin, tenant, "Finance");
    var lead = await TestAccounts.CreateUserAsync(server, "lead@example.com", tenant);
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    await TestPermissions.AssignAsync(server, lead, PermissionNames.UserGroupAssignUsers, PermissionScopeKind.UserGroup, support.Id);
    using var leadClient = await ClientForAsync(server, "lead@example.com");

    var own = await leadClient.PostAsJsonAsync(Members(support.Id, tenant), new UserGroupMembersRequest([tech.Id]), Cancel);
    var other = await leadClient.PostAsJsonAsync(Members(finance.Id, tenant), new UserGroupMembersRequest([tech.Id]), Cancel);
    var rename = await leadClient.PutAsJsonAsync(Group(support.Id, tenant), new UserGroupRequest("Mine"), Cancel);

    Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
  }

  [Fact]
  public async Task AnotherTenantsGroup_IsNotFound()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (first, firstTenant) = await RegisterAsync(server, "first@example.com");
    var (second, secondTenant) = await RegisterAsync(server, "second@example.com");
    var group = await CreateAsync(first, firstTenant, "Support");

    Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync(Group(group.Id, secondTenant), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await second.PutAsJsonAsync(Group(group.Id, secondTenant), new UserGroupRequest("Mine"), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await second.DeleteAsync(Group(group.Id, secondTenant), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await second.PostAsJsonAsync(Members(group.Id, secondTenant), new UserGroupMembersRequest([]), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await second.GetAsync(Groups(firstTenant), Cancel)).StatusCode);
    Assert.Equal(1, await server.WithDbAsync(db => db.UserGroups.IgnoreQueryFilters().CountAsync(x => x.Name == "Support")));
  }

  [Fact]
  public async Task WithoutThePermissions_EveryRouteIsForbidden()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var group = await CreateAsync(admin, tenant, "Support");
    await TestAccounts.CreateUserAsync(server, "plain@example.com", tenant);
    using var plain = await ClientForAsync(server, "plain@example.com");

    Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync(Groups(tenant), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync(Group(group.Id, tenant), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsJsonAsync(Groups(tenant), new UserGroupRequest("x"), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await plain.PutAsJsonAsync(Group(group.Id, tenant), new UserGroupRequest("x"), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await plain.DeleteAsync(Group(group.Id, tenant), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsJsonAsync(Members(group.Id, tenant), new UserGroupMembersRequest([]), Cancel)).StatusCode);
  }

  private static string Groups(Guid tenant) => $"{Routes.UserGroups}?tenantId={tenant}";

  private static string Group(Guid id, Guid tenant) => $"{Routes.UserGroup(id)}?tenantId={tenant}";

  private static string Members(Guid id, Guid tenant) => $"{Routes.UserGroupMembers(id)}?tenantId={tenant}";

  private static async Task<UserGroupDetail> CreateAsync(HttpClient client, Guid tenant, string name, string? description = null)
  {
    var response = await client.PostAsJsonAsync(Groups(tenant), new UserGroupRequest(name, description), Cancel);
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<UserGroupDetail>(Cancel))!;
  }

  private static Task GrantGroupAsync(ServerHost server, Guid groupId, Guid tenant, string permission) => server.WithDbAsync(async db =>
  {
    db.PermissionAssignments.Add(new PermissionAssignmentRecord
    {
      PrincipalKind = PermissionPrincipalKind.UserGroup,
      PrincipalId = groupId,
      Permission = permission,
      Effect = PermissionEffect.Allow,
      ScopeKind = PermissionScopeKind.Tenant,
      ScopeId = tenant,
      OwningTenantId = tenant,
    });
    return await db.SaveChangesAsync();
  });

  private static Task<List<AuthorizationChangeRecord>> LogAsync(ServerHost server, Guid groupId) =>
    server.WithDbAsync(db => db.AuthorizationChanges.Where(x => x.TargetId == groupId).OrderBy(x => x.CreatedAt).ToListAsync());

  private static async Task<IReadOnlyList<string>> HeldAsync(HttpClient client) =>
    (await client.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel))!.Permissions;

  private static async Task<(HttpClient Client, Guid TenantId)> RegisterAsync(ServerHost server, string email)
  {
    using var anonymous = TestAccounts.Client(server);
    var response = await anonymous.PostAsJsonAsync($"{Routes.Auth}/register", new { email, password = TestAccounts.Password }, Cancel);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var client = await ClientForAsync(server, email);
    return (client, (await client.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel))!.TenantId);
  }

  private static async Task<HttpClient> ClientForAsync(ServerHost server, string email)
  {
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
    return client;
  }
}
