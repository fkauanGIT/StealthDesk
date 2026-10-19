using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using StealthDesk.Contracts.DeviceGroups;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Contracts.UserGroups;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Server.Tests;

/// <summary>
/// A deny wins over any allow, wherever each comes from: the user, one of their groups, or another group, and
/// whether it names a device, a device group or the whole tenant. Set up through the API as an administrator would,
/// and checked on every way to read a device: the list, the device itself and live updates.
/// </summary>
public class DenyPrecedenceTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task ADenyFromOneUserGroup_BeatsAnAllowFromAnother()
  {
    await using var setup = await Setup.CreateAsync();
    var support = await setup.UserGroupAsync("Support", setup.Tech);
    var restricted = await setup.UserGroupAsync("Restricted", setup.Tech);
    await setup.GrantAsync(PermissionPrincipalKind.UserGroup, support, PermissionEffect.Allow, PermissionScopeKind.Tenant, setup.Tenant);
    await setup.GrantAsync(PermissionPrincipalKind.UserGroup, restricted, PermissionEffect.Deny, PermissionScopeKind.Device, setup.Finance);

    await setup.AssertReadsOnlyAsync(setup.Front);
  }

  [Fact]
  public async Task ADenyOnADeviceGroup_HidesEveryDeviceInIt()
  {
    await using var setup = await Setup.CreateAsync();
    var finance = await setup.DeviceGroupAsync("Finance PCs", setup.Finance);
    var support = await setup.UserGroupAsync("Support", setup.Tech);
    var restricted = await setup.UserGroupAsync("Restricted", setup.Tech);
    await setup.GrantAsync(PermissionPrincipalKind.UserGroup, support, PermissionEffect.Allow, PermissionScopeKind.Tenant, setup.Tenant);
    await setup.GrantAsync(PermissionPrincipalKind.UserGroup, restricted, PermissionEffect.Deny, PermissionScopeKind.DeviceGroup, finance);

    await setup.AssertReadsOnlyAsync(setup.Front);
  }

  // The narrower grant doesn't win: a deny for the whole tenant leaves nothing, even a device allowed by name.
  [Fact]
  public async Task ATenantDenyFromAGroup_BeatsTheUsersOwnAllowOnADevice()
  {
    await using var setup = await Setup.CreateAsync();
    var restricted = await setup.UserGroupAsync("Restricted", setup.Tech);
    await setup.GrantAsync(PermissionPrincipalKind.User, setup.Tech, PermissionEffect.Allow, PermissionScopeKind.Device, setup.Front);
    await setup.GrantAsync(PermissionPrincipalKind.UserGroup, restricted, PermissionEffect.Deny, PermissionScopeKind.Tenant, setup.Tenant);

    await setup.AssertReadsOnlyAsync();
  }

  // The deny follows group membership: leaving the group gives the allowed device back on the next request.
  [Fact]
  public async Task LeavingTheDenyingGroup_GivesTheDeviceBack()
  {
    await using var setup = await Setup.CreateAsync();
    var support = await setup.UserGroupAsync("Support", setup.Tech);
    var restricted = await setup.UserGroupAsync("Restricted", setup.Tech);
    await setup.GrantAsync(PermissionPrincipalKind.UserGroup, support, PermissionEffect.Allow, PermissionScopeKind.Tenant, setup.Tenant);
    await setup.GrantAsync(PermissionPrincipalKind.UserGroup, restricted, PermissionEffect.Deny, PermissionScopeKind.Device, setup.Finance);
    await setup.AssertReadsOnlyAsync(setup.Front);

    await setup.LeaveAsync(restricted, setup.Tech);

    await setup.AssertReadsOnlyAsync(setup.Front, setup.Finance);
  }

  /// <summary>A tenant with two devices, an administrator, and a technician with no permissions of their own.</summary>
  private sealed class Setup : IAsyncDisposable
  {
    private ServerHost _server = null!;
    private HttpClient _admin = null!;
    private HttpClient _tech = null!;
    private string _techCookie = null!;

    public Guid Tenant { get; private set; }

    public Guid Tech { get; private set; }

    public Guid Front { get; private set; }

    public Guid Finance { get; private set; }

    private string InTenant => $"?tenantId={Tenant}";

    public static async Task<Setup> CreateAsync()
    {
      var server = await ServerHost.OnPostgresAsync();
      var setup = new Setup { _server = server, Tenant = await TestTenants.CreateAsync(server) };
      setup.Front = await setup.DeviceAsync("FRONT-DESK");
      setup.Finance = await setup.DeviceAsync("FINANCE-01");

      var admin = await TestAccounts.CreateUserAsync(server, "admin@example.com", setup.Tenant);
      foreach (var permission in PermissionCatalog.All.Where(x => x.AllowsTenantScope))
      {
        await TestPermissions.AssignAsync(server, admin, permission.Name, PermissionScopeKind.Tenant, setup.Tenant);
      }

      setup._admin = await ClientAsync(server, "admin@example.com");
      setup.Tech = (await TestAccounts.CreateUserAsync(server, "tech@example.com", setup.Tenant)).Id;
      setup._techCookie = await TestAccounts.SignInForCookieAsync(server, "tech@example.com");
      setup._tech = TestAccounts.Client(server);
      setup._tech.DefaultRequestHeaders.Add("Cookie", setup._techCookie);
      return setup;
    }

    public async Task<Guid> UserGroupAsync(string name, Guid member)
    {
      var group = await CreatedAsync<UserGroupDetail>(Routes.UserGroups + InTenant, new UserGroupRequest(name));
      await SucceedsAsync(_admin.PostAsJsonAsync(Routes.UserGroupMembers(group.Id) + InTenant, new UserGroupMembersRequest([member]), Cancel));
      return group.Id;
    }

    public async Task<Guid> DeviceGroupAsync(string name, Guid device)
    {
      var group = await CreatedAsync<DeviceGroupDetail>(Routes.DeviceGroups + InTenant, new DeviceGroupRequest(name));
      await SucceedsAsync(_admin.PostAsJsonAsync(Routes.DeviceGroupMembers(group.Id) + InTenant, new DeviceGroupMembersRequest([device]), Cancel));
      return group.Id;
    }

    public Task LeaveAsync(Guid group, Guid member) =>
      SucceedsAsync(_admin.SendAsync(
        new HttpRequestMessage(HttpMethod.Delete, Routes.UserGroupMembers(group) + InTenant) { Content = JsonContent.Create(new UserGroupMembersRequest([member])) },
        Cancel));

    public Task GrantAsync(PermissionPrincipalKind kind, Guid principal, PermissionEffect effect, PermissionScopeKind scope, Guid scopeId) =>
      CreatedAsync<PermissionAssignment>(
        Routes.PermissionAssignments + InTenant,
        new CreatePermissionAssignmentRequest(kind, principal, PermissionNames.DeviceRead, effect, scope, scopeId));

    /// <summary>The technician reads exactly these devices, the same way through the list, each device and live updates.</summary>
    public async Task AssertReadsOnlyAsync(params Guid[] readable)
    {
      var listed = (await _tech.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, Cancel))!.Select(x => x.Id);
      Assert.Equal(readable.Order(), listed.Order());

      foreach (var device in new[] { Front, Finance })
      {
        using var one = await _tech.GetAsync(Routes.Device(device), Cancel);
        Assert.Equal(readable.Contains(device) ? HttpStatusCode.OK : HttpStatusCode.NotFound, one.StatusCode);
      }

      await using var dashboard = await TestDashboard.ConnectAsync(_server, _techCookie);
      var reply = await dashboard.Connection.InvokeAsync<GatewayReply<IReadOnlyList<Guid>>>(nameof(IDashboardHub.SubscribeToDevices), new[] { Front, Finance }, Cancel);
      Assert.True(reply.Accepted, reply.Error);
      Assert.Equal(readable.Order(), reply.Value!.Order());
    }

    public ValueTask DisposeAsync()
    {
      _admin.Dispose();
      _tech.Dispose();
      return _server.DisposeAsync();
    }

    private Task<Guid> DeviceAsync(string name) => _server.WithDbAsync(async db =>
    {
      var device = new DeviceRecord { Id = Guid.NewGuid(), TenantId = Tenant, Name = name };
      db.Devices.Add(device);
      await db.SaveChangesAsync();
      return device.Id;
    });

    private async Task<T> CreatedAsync<T>(string path, object body)
    {
      using var response = await _admin.PostAsync(path, JsonContent.Create(body, body.GetType()), Cancel);
      Assert.True(response.StatusCode == HttpStatusCode.Created, $"POST {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Cancel)}");
      return (await response.Content.ReadFromJsonAsync<T>(Cancel))!;
    }

    private static async Task SucceedsAsync(Task<HttpResponseMessage> sending)
    {
      using var response = await sending;
      Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Cancel)}");
    }

    private static async Task<HttpClient> ClientAsync(ServerHost server, string email)
    {
      var client = TestAccounts.Client(server);
      client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
      return client;
    }
  }
}
