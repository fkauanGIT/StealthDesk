using System.Net;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.DeviceGroups;
using StealthDesk.Contracts.InstallerKeys;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>
/// Device groups: managed and filled per tenant with one log entry per change; device permissions granted on a group
/// reaching exactly its devices, denies included; installer keys placing new devices in a group.
/// </summary>
public class DeviceGroupApiTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task AGroupsLife_LeavesOneEntryPerChange()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var pc = await DeviceAsync(server, tenant, "PC-1");

    var group = await CreateAsync(admin, tenant, new DeviceGroupRequest(" Sales ", " Sales floor "));
    var duplicate = await admin.PostAsJsonAsync(All(tenant), new DeviceGroupRequest("Sales"), Cancel);
    var renamed = await admin.PutAsJsonAsync(One(group.Id, tenant), new DeviceGroupRequest("Sales team", null), Cancel);
    var added = await admin.PostAsJsonAsync(Members(group.Id, tenant), new DeviceGroupMembersRequest([pc]), Cancel);
    var addedAgain = await admin.PostAsJsonAsync(Members(group.Id, tenant), new DeviceGroupMembersRequest([pc]), Cancel);
    var detail = await admin.GetFromJsonAsync<DeviceGroupDetail>(One(group.Id, tenant), Cancel);
    var listed = (await admin.GetFromJsonAsync<DeviceGroupList>(All(tenant), Cancel))!.Items;
    var removed = await SendAsync(admin, HttpMethod.Delete, Members(group.Id, tenant), new DeviceGroupMembersRequest([pc]));
    var deleted = await admin.DeleteAsync(One(group.Id, tenant), Cancel);

    Assert.Equal("Sales", group.Name);
    Assert.Equal("Sales floor", group.Description);
    Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, addedAgain.StatusCode);
    Assert.Equal("PC-1", Assert.Single(detail!.Devices).Name);
    Assert.Equal(1, Assert.Single(listed).DeviceCount);
    Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    var log = await server.WithDbAsync(db => db.AuthorizationChanges.Where(x => x.TargetKind == AuthorizationChangeTargets.DeviceGroup).OrderBy(x => x.CreatedAt).Select(x => x.Action).ToListAsync());
    Assert.Equal(
      [
        AuthorizationChangeActions.DeviceGroupCreated,
        AuthorizationChangeActions.DeviceGroupUpdated,
        AuthorizationChangeActions.DeviceGroupMembersAdded,
        AuthorizationChangeActions.DeviceGroupMembersRemoved,
        AuthorizationChangeActions.DeviceGroupDeleted,
      ],
      log);
    Assert.True(await server.WithDbAsync(db => db.Devices.AnyAsync(x => x.Id == pc)));
  }

  [Fact]
  public async Task MovingDevices_IsOneChange_AndNeedsBothGroups()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var pc = await DeviceAsync(server, tenant, "PC-1");
    var sales = await CreateAsync(admin, tenant, new DeviceGroupRequest("Sales"));
    var hr = await CreateAsync(admin, tenant, new DeviceGroupRequest("HR"));
    await admin.PostAsJsonAsync(Members(sales.Id, tenant), new DeviceGroupMembersRequest([pc]), Cancel);
    var helper = await TestAccounts.CreateUserAsync(server, "helper@example.com", tenant);
    await TestPermissions.AssignAsync(server, helper, PermissionNames.DeviceGroupAssignDevices, PermissionScopeKind.DeviceGroup, hr.Id);
    using var helperClient = await ClientForAsync(server, "helper@example.com");

    var notTheirs = await helperClient.PostAsJsonAsync(Members(hr.Id, tenant), new DeviceGroupMembersRequest([pc], FromGroupId: sales.Id), Cancel);
    var intoTheirs = await helperClient.PostAsJsonAsync(Members(hr.Id, tenant), new DeviceGroupMembersRequest([pc]), Cancel);
    var moved = await admin.PostAsJsonAsync(Members(hr.Id, tenant), new DeviceGroupMembersRequest([pc], FromGroupId: sales.Id), Cancel);
    var ontoItself = await admin.PostAsJsonAsync(Members(hr.Id, tenant), new DeviceGroupMembersRequest([pc], FromGroupId: hr.Id), Cancel);

    Assert.Equal(HttpStatusCode.Forbidden, notTheirs.StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, intoTheirs.StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, ontoItself.StatusCode);
    Assert.Equal([hr.Id], await server.WithDbAsync(db => db.DeviceGroupMembers.Where(x => x.DeviceId == pc).Select(x => x.DeviceGroupId).ToListAsync()));
    var move = await server.WithDbAsync(db => db.AuthorizationChanges.SingleAsync(x => x.Action == AuthorizationChangeActions.DeviceGroupMembersMoved));
    Assert.Contains($"\"fromGroupId\":\"{sales.Id}\"", move.AfterJson);
  }

  [Fact]
  public async Task AnotherTenantsGroupsAndDevices_AreOutOfReach()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("Accounts:EnablePublicRegistration", "true");
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var (other, otherTenant) = await RegisterAsync(server, "other@example.com");
    var group = await CreateAsync(admin, tenant, new DeviceGroupRequest("Sales"));
    var theirDevice = await DeviceAsync(server, otherTenant, "THEIRS");

    var foreignDevice = await admin.PostAsJsonAsync(Members(group.Id, tenant), new DeviceGroupMembersRequest([theirDevice]), Cancel);
    var foreignGroup = await other.GetAsync(Routes.DeviceGroup(group.Id), Cancel);
    var foreignDelete = await other.DeleteAsync(Routes.DeviceGroup(group.Id), Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, foreignDevice.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, foreignGroup.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.DeviceGroupMembers.AnyAsync()));
  }

  [Fact]
  public async Task ReadingOnAGroup_ShowsExactlyItsDevices_WhileTheyAreInIt()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var inGroup = await DeviceAsync(server, tenant, "IN-GROUP");
    var outside = await DeviceAsync(server, tenant, "OUTSIDE");
    var sales = await CreateAsync(admin, tenant, new DeviceGroupRequest("Sales"));
    await admin.PostAsJsonAsync(Members(sales.Id, tenant), new DeviceGroupMembersRequest([inGroup]), Cancel);
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    await TestPermissions.AssignAsync(server, tech, PermissionNames.DeviceRead, PermissionScopeKind.DeviceGroup, sales.Id);
    using var techClient = await ClientForAsync(server, "tech@example.com");

    var listed = await techClient.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, Cancel);
    var one = await techClient.GetAsync(Routes.Device(inGroup), Cancel);
    var other = await techClient.GetAsync(Routes.Device(outside), Cancel);
    await SendAsync(admin, HttpMethod.Delete, Members(sales.Id, tenant), new DeviceGroupMembersRequest([inGroup]));
    var afterLeaving = await techClient.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, Cancel);
    var oneAfterLeaving = await techClient.GetAsync(Routes.Device(inGroup), Cancel);

    var seen = Assert.Single(listed!);
    Assert.Equal("IN-GROUP", seen.Name);
    Assert.Equal([sales.Id], seen.GroupIds);
    Assert.Equal(HttpStatusCode.OK, one.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    Assert.Empty(afterLeaving!);
    Assert.Equal(HttpStatusCode.NotFound, oneAfterLeaving.StatusCode);
  }

  [Fact]
  public async Task ADenyOnAGroup_HidesItsDevices_FromSomeoneWhoReadsTheWholeTenant()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var hidden = await DeviceAsync(server, tenant, "HIDDEN");
    await DeviceAsync(server, tenant, "VISIBLE");
    var hr = await CreateAsync(admin, tenant, new DeviceGroupRequest("HR"));
    await admin.PostAsJsonAsync(Members(hr.Id, tenant), new DeviceGroupMembersRequest([hidden]), Cancel);
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    await TestPermissions.AllowTenantDevicesAsync(server, tech);
    await TestPermissions.AssignAsync(server, tech, PermissionNames.DeviceRead, PermissionScopeKind.DeviceGroup, hr.Id, PermissionEffect.Deny);
    using var techClient = await ClientForAsync(server, "tech@example.com");

    var listed = await techClient.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, Cancel);
    var one = await techClient.GetAsync(Routes.Device(hidden), Cancel);

    Assert.Equal(["VISIBLE"], listed!.Select(x => x.Name));
    Assert.Equal(HttpStatusCode.NotFound, one.StatusCode);
  }

  [Fact]
  public async Task AGroupScope_NeedsAGroupOfTheTenant_AndGoesWithTheGroup()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var tech = await TestAccounts.CreateUserAsync(server, "tech@example.com", tenant);
    var sales = await CreateAsync(admin, tenant, new DeviceGroupRequest("Sales"));

    var unknown = await admin.PostAsJsonAsync($"{Routes.PermissionAssignments}?tenantId={tenant}",
      new CreatePermissionAssignmentRequest(PermissionPrincipalKind.User, tech.Id, PermissionNames.DeviceRead, PermissionEffect.Allow, PermissionScopeKind.DeviceGroup, Guid.NewGuid()), Cancel);
    var granted = await admin.PostAsJsonAsync($"{Routes.PermissionAssignments}?tenantId={tenant}",
      new CreatePermissionAssignmentRequest(PermissionPrincipalKind.User, tech.Id, PermissionNames.DeviceRead, PermissionEffect.Allow, PermissionScopeKind.DeviceGroup, sales.Id), Cancel);
    var notAtGroup = await admin.PostAsJsonAsync($"{Routes.PermissionAssignments}?tenantId={tenant}",
      new CreatePermissionAssignmentRequest(PermissionPrincipalKind.User, tech.Id, PermissionNames.TenantUsersRead, PermissionEffect.Allow, PermissionScopeKind.DeviceGroup, sales.Id), Cancel);
    await admin.DeleteAsync(One(sales.Id, tenant), Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, notAtGroup.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.PermissionAssignments.AnyAsync(x => x.ScopeKind == PermissionScopeKind.DeviceGroup)));
  }

  [Fact]
  public async Task ManagingGroups_TakesTheirPermissions()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var group = await CreateAsync(admin, tenant, new DeviceGroupRequest("Sales"));
    var reader = await TestAccounts.CreateUserAsync(server, "reader@example.com", tenant);
    await TestPermissions.AssignAsync(server, reader, PermissionNames.TenantDeviceGroupsRead, PermissionScopeKind.Tenant, tenant);
    using var readerClient = await ClientForAsync(server, "reader@example.com");

    Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync(All(tenant), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync(All(tenant), new DeviceGroupRequest("Mine"), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.DeleteAsync(One(group.Id, tenant), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync(Members(group.Id, tenant), new DeviceGroupMembersRequest([]), Cancel)).StatusCode);
  }

  [Fact]
  public async Task AKeyWithAGroup_PutsNewDevicesInIt_ButNotReinstalledOnes()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var sales = await CreateAsync(admin, tenant, new DeviceGroupRequest("Sales"));
    var created = await admin.PostAsJsonAsync($"{Routes.InstallerKeys}?tenantId={tenant}", new CreateInstallerKeyRequest(InstallerKeyType.Persistent, "Sales PCs", DeviceGroupId: sales.Id), Cancel);
    var key = (await created.Content.ReadFromJsonAsync<CreatedInstallerKey>(Cancel))!;
    await using var agent = await TestAgent.ConnectAsync(server, withInstallerKey: false);
    var deviceId = Guid.NewGuid();

    var joined = await agent.ReportAsync(TestAgent.Report(deviceId) with { InstallerKey = key.FullKey });
    await SendAsync(admin, HttpMethod.Delete, Members(sales.Id, tenant), new DeviceGroupMembersRequest([deviceId]));
    await using var reinstalled = await TestAgent.ConnectAsync(server, withInstallerKey: false);
    var again = await reinstalled.ReportAsync(TestAgent.Report(deviceId) with { InstallerKey = key.FullKey });

    Assert.Equal(sales.Id, key.Key.DeviceGroupId);
    Assert.Equal("Sales", key.Key.DeviceGroupName);
    Assert.True(joined.Accepted, joined.Error);
    Assert.True(again.Accepted, again.Error);
    Assert.False(await server.WithDbAsync(db => db.DeviceGroupMembers.AnyAsync()));
    var placed = await server.WithDbAsync(db => db.AuthorizationChanges.OrderBy(x => x.CreatedAt).FirstAsync(x => x.Action == AuthorizationChangeActions.DeviceGroupMembersAdded));
    Assert.Equal(AuthorizationChangeActors.System, placed.ActorKind);
    Assert.Contains($"\"installerKeyId\":\"{key.Key.Id}\"", placed.AfterJson);
  }

  [Fact]
  public async Task AKeysGroup_MustBeOneItsCreatorMayFill()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var sales = await CreateAsync(admin, tenant, new DeviceGroupRequest("Sales"));
    var installer = await TestAccounts.CreateUserAsync(server, "installer@example.com", tenant);
    await TestPermissions.AssignAsync(server, installer, PermissionNames.InstallerKeyWrite, PermissionScopeKind.Tenant, tenant);
    using var installerClient = await ClientForAsync(server, "installer@example.com");

    var notTheirs = await installerClient.PostAsJsonAsync($"{Routes.InstallerKeys}?tenantId={tenant}", new CreateInstallerKeyRequest(InstallerKeyType.Persistent, DeviceGroupId: sales.Id), Cancel);
    var unknown = await admin.PostAsJsonAsync($"{Routes.InstallerKeys}?tenantId={tenant}", new CreateInstallerKeyRequest(InstallerKeyType.Persistent, DeviceGroupId: Guid.NewGuid()), Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, notTheirs.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.InstallerKeys.AnyAsync()));
  }

  [Fact]
  public async Task ExistingAdministrators_GetTheNewPermissions_WhenGroupsArrive()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var admin = await TestAccounts.CreateUserAsync(server, "admin@example.com");
    var plain = await TestAccounts.CreateUserAsync(server, "plain@example.com");
    await TestPermissions.AssignAsync(server, admin, PermissionNames.TenantUserGroupsWrite, PermissionScopeKind.Tenant, admin.TenantId);

    await MigrateAsync(server, "20261015184544_AddServerAlert");
    await MigrateAsync(server, null);
    var held = await server.WithDbAsync(db => db.PermissionAssignments
      .Where(x => x.Permission.Contains("device-group")).Select(x => new { x.PrincipalId, x.Permission, x.ScopeId }).ToListAsync());

    Assert.Equal(
      [PermissionNames.DeviceGroupAssignDevices, PermissionNames.TenantDeviceGroupsRead, PermissionNames.TenantDeviceGroupsWrite],
      held.Where(x => x.PrincipalId == admin.Id).Select(x => x.Permission).Order());
    Assert.All(held, x => Assert.Equal(admin.TenantId, x.ScopeId));
    Assert.DoesNotContain(held, x => x.PrincipalId == plain.Id);
  }

  private static string All(Guid tenant) => $"{Routes.DeviceGroups}?tenantId={tenant}";

  private static string One(Guid id, Guid tenant) => $"{Routes.DeviceGroup(id)}?tenantId={tenant}";

  private static string Members(Guid id, Guid tenant) => $"{Routes.DeviceGroupMembers(id)}?tenantId={tenant}";

  private static async Task<DeviceGroupDetail> CreateAsync(HttpClient client, Guid tenant, DeviceGroupRequest request)
  {
    var response = await client.PostAsJsonAsync(All(tenant), request, Cancel);
    Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Cancel));
    return (await response.Content.ReadFromJsonAsync<DeviceGroupDetail>(Cancel))!;
  }

  private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object body)
  {
    using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
    return await client.SendAsync(request, Cancel);
  }

  private static Task<Guid> DeviceAsync(ServerHost server, Guid tenant, string name) => server.WithDbAsync(async db =>
  {
    var device = new DeviceRecord { Id = Guid.NewGuid(), TenantId = tenant, Name = name };
    db.Devices.Add(device);
    await db.SaveChangesAsync();
    return device.Id;
  });

  private static async Task MigrateAsync(ServerHost server, string? target)
  {
    await using var scope = server.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<StealthDeskDb>().GetService<IMigrator>().MigrateAsync(target, Cancel);
  }

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
