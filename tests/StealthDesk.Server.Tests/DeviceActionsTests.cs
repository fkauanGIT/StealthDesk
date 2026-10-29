using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Contracts.ServiceAccounts;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>
/// The device actions the dashboard hub reports, through a signed-in browser connection: the terminal allowed and
/// denied at every scope it can be granted at, denies first, and nothing for a device the user can't read.
/// </summary>
public class DeviceActionsTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  public static TheoryData<PermissionScopeKind> TenantScopes => [PermissionScopeKind.Device, PermissionScopeKind.DeviceGroup, PermissionScopeKind.Tenant];

  public static TheoryData<PermissionScopeKind> Scopes => [.. TenantScopes, PermissionScopeKind.Server];

  // Allowed at server scope is the next test: only a server's own principals can hold that.
  [Theory]
  [MemberData(nameof(TenantScopes))]
  public async Task TheTerminal_AllowedAtAScope_IsOffered(PermissionScopeKind scope)
  {
    await using var world = await World.CreateAsync();
    await world.AssignAsync(PermissionNames.DeviceTerminalUse, scope);

    var actions = await world.ActionsAsync();

    Assert.True(actions.Accepted, actions.Error);
    Assert.True(actions.Value!.UseTerminal);
  }

  [Theory]
  [MemberData(nameof(Scopes))]
  public async Task TheTerminal_DeniedAtAScope_IsNotOffered_EvenAllowedForTheTenant(PermissionScopeKind scope)
  {
    await using var world = await World.CreateAsync();
    await world.AssignAsync(PermissionNames.DeviceTerminalUse, PermissionScopeKind.Tenant);
    await world.AssignAsync(PermissionNames.DeviceTerminalUse, scope, PermissionEffect.Deny);

    var actions = await world.ActionsAsync();

    Assert.True(actions.Accepted, actions.Error);
    Assert.False(actions.Value!.UseTerminal);
  }

  [Fact]
  public async Task TheTerminal_AllowedAtServerScope_IsOfferedToAServerServiceAccount_InEveryTenant()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("Accounts:EnablePublicRegistration", "true");
    using var admin = await RegisterAsync(server, "admin@example.com");
    using var other = await RegisterAsync(server, "other@example.com");
    var device = await DeviceAsync(server, (await MeAsync(other)).TenantId);
    var tenant = (await MeAsync(admin)).TenantId;
    var account = await SucceedsAsync<ServiceAccount>(admin.PostAsJsonAsync(Routes.ServerServiceAccounts, new CreateServiceAccountRequest("Fleet"), Cancel));
    foreach (var permission in new[] { PermissionNames.DeviceRead, PermissionNames.DeviceTerminalUse })
    {
      await SucceedsAsync<PermissionAssignment>(admin.PostAsJsonAsync(
        $"{Routes.PermissionAssignments}?tenantId={tenant}",
        new CreatePermissionAssignmentRequest(PermissionPrincipalKind.ServiceAccount, account.Id, permission, PermissionEffect.Allow, PermissionScopeKind.Server),
        Cancel));
    }

    var key = await SucceedsAsync<CreatedServiceAccountCredential>(admin.PostAsJsonAsync(
      Routes.ServiceAccountCredentials(Routes.ServerServiceAccounts, account.Id), new CreateServiceAccountCredentialRequest("Primary", null), Cancel));
    await using var dashboard = await TestDashboard.ConnectAsync(server, headers: new Dictionary<string, string> { [ServiceAccountHeader.Name] = key.ApiKey });

    var actions = await dashboard.Connection.InvokeAsync<GatewayReply<DeviceActions>>(nameof(IDashboardHub.GetDeviceActions), device, Cancel);

    Assert.True(actions.Accepted, actions.Error);
    Assert.True(actions.Value!.UseTerminal);
  }

  [Fact]
  public async Task TheTerminal_AllowedAtServerScope_DoesNothingForAUserOfATenant()
  {
    await using var world = await World.CreateAsync();
    await world.AssignAsync(PermissionNames.DeviceTerminalUse, PermissionScopeKind.Server);

    var actions = await world.ActionsAsync();

    Assert.True(actions.Accepted, actions.Error);
    Assert.False(actions.Value!.UseTerminal);
  }

  // Each remote control permission and the action it offers.
  private static readonly Dictionary<string, Func<DeviceActions, bool>> RemoteControlActions = new()
  {
    [PermissionNames.DeviceRemoteControlConnect] = x => x.SeeDesktop,
    [PermissionNames.DeviceRemoteControlInteract] = x => x.ControlDesktop,
    [PermissionNames.DeviceRemoteControlElevatedDesktop] = x => x.UseSecureScreens,
    [PermissionNames.DeviceCtrlAltDelSend] = x => x.SendCtrlAltDel,
    [PermissionNames.DeviceClipboardRead] = x => x.ReadClipboard,
    [PermissionNames.DeviceClipboardWrite] = x => x.WriteClipboard,
  };

  public static TheoryData<string> RemoteControlPermissions => [.. RemoteControlActions.Keys];

  [Theory]
  [MemberData(nameof(RemoteControlPermissions))]
  public async Task ARemoteControlPermission_OnTheDevice_OffersItsActionAndNoOther(string permission)
  {
    await using var world = await World.CreateAsync();
    await world.AssignAsync(permission, PermissionScopeKind.Device);

    var actions = await world.ActionsAsync();

    Assert.True(actions.Accepted, actions.Error);
    Assert.All(RemoteControlActions, x => Assert.Equal(x.Key == permission, x.Value(actions.Value!)));
    Assert.False(actions.Value!.UseTerminal);
  }

  [Theory]
  [MemberData(nameof(RemoteControlPermissions))]
  public async Task ARemoteControlPermission_DeniedOnTheDevice_IsNotOffered_EvenAllowedForTheTenant(string permission)
  {
    await using var world = await World.CreateAsync();
    await world.AssignAsync(permission, PermissionScopeKind.Tenant);
    await world.AssignAsync(permission, PermissionScopeKind.Device, PermissionEffect.Deny);

    var actions = await world.ActionsAsync();

    Assert.True(actions.Accepted, actions.Error);
    Assert.False(RemoteControlActions[permission](actions.Value!));
  }

  [Fact]
  public async Task ReadingAlone_OffersNoTerminal()
  {
    await using var world = await World.CreateAsync();

    var actions = await world.ActionsAsync();

    Assert.True(actions.Accepted, actions.Error);
    Assert.False(actions.Value!.UseTerminal);
    Assert.All(RemoteControlActions.Values, x => Assert.False(x(actions.Value!)));
  }

  [Fact]
  public async Task TheTerminalWithoutReading_IsRefusedLikeAMissingDevice()
  {
    await using var world = await World.CreateAsync(canRead: false);
    await world.AssignAsync(PermissionNames.DeviceTerminalUse, PermissionScopeKind.Tenant);

    var actions = await world.ActionsAsync();
    var missing = await world.ActionsAsync(Guid.NewGuid());

    Assert.False(actions.Accepted);
    Assert.Equal("Device not found.", actions.Error);
    Assert.Equal(actions.Error, missing.Error);
  }

  [Fact]
  public async Task SomeoneOfAnotherTenant_HoldingEverythingThere_GetsNothingOfThisDevice()
  {
    await using var world = await World.CreateAsync(canRead: false, ownTenant: false);
    await world.AssignAsync(PermissionNames.DeviceRead, PermissionScopeKind.Tenant);
    await world.AssignAsync(PermissionNames.DeviceTerminalUse, PermissionScopeKind.Tenant);

    var actions = await world.ActionsAsync();

    Assert.False(actions.Accepted);
    Assert.Equal("Device not found.", actions.Error);
  }

  private static async Task<HttpClient> RegisterAsync(ServerHost server, string email)
  {
    using var anonymous = TestAccounts.Client(server);
    using var registered = await anonymous.PostAsJsonAsync($"{Routes.Auth}/register", new { email, password = TestAccounts.Password }, Cancel);
    Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
    return client;
  }

  private static async Task<CurrentUser> MeAsync(HttpClient client) =>
    (await client.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel))!;

  private static Task<Guid> DeviceAsync(ServerHost server, Guid tenant) => server.WithDbAsync(async db =>
  {
    var device = new DeviceRecord { Id = Guid.NewGuid(), TenantId = tenant, Name = "BRANCH-PC" };
    db.Devices.Add(device);
    await db.SaveChangesAsync();
    return device.Id;
  });

  private static async Task<T> SucceedsAsync<T>(Task<HttpResponseMessage> sending)
  {
    using var response = await sending;
    Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Cancel)}");
    return (await response.Content.ReadFromJsonAsync<T>(Cancel))!;
  }

  /// <summary>One device in a group of the tenant, and a signed-in user, of that tenant or another, holding nothing else.</summary>
  private sealed class World : IAsyncDisposable
  {
    private ServerHost _server = null!;
    private UserRecord _user = null!;
    private TestDashboard _dashboard = null!;
    private Guid _group;

    public Guid Device { get; private set; }

    public static async Task<World> CreateAsync(bool canRead = true, bool ownTenant = true)
    {
      var server = await ServerHost.OnPostgresAsync();
      var tenant = await TestTenants.CreateAsync(server, "Tenant");
      var world = new World { _server = server, Device = Guid.NewGuid(), _group = Guid.NewGuid() };
      await server.WithDbAsync(db =>
      {
        db.Devices.Add(new DeviceRecord { Id = world.Device, TenantId = tenant, Name = "FRONT-DESK" });
        db.DeviceGroups.Add(new DeviceGroupRecord { Id = world._group, TenantId = tenant, Name = "Front" });
        db.DeviceGroupMembers.Add(new DeviceGroupMemberRecord { DeviceGroupId = world._group, DeviceId = world.Device });
        return db.SaveChangesAsync();
      });

      var userTenant = ownTenant ? tenant : await TestTenants.CreateAsync(server, "Other tenant");
      world._user = await TestAccounts.CreateUserAsync(server, "tech@example.com", userTenant);
      if (canRead)
      {
        await world.AssignAsync(PermissionNames.DeviceRead, PermissionScopeKind.Tenant);
      }

      world._dashboard = await TestDashboard.ConnectAsync(server, await TestAccounts.SignInForCookieAsync(server, "tech@example.com"));
      return world;
    }

    public Task AssignAsync(string permission, PermissionScopeKind scope, PermissionEffect effect = PermissionEffect.Allow) =>
      TestPermissions.AssignAsync(_server, _user, permission, scope, scope switch
      {
        PermissionScopeKind.Device => Device,
        PermissionScopeKind.DeviceGroup => _group,
        _ => _user.TenantId,
      }, effect);

    public Task<GatewayReply<DeviceActions>> ActionsAsync(Guid? device = null) =>
      _dashboard.Connection.InvokeAsync<GatewayReply<DeviceActions>>(nameof(IDashboardHub.GetDeviceActions), device ?? Device, Cancel);

    public async ValueTask DisposeAsync()
    {
      await _dashboard.DisposeAsync();
      _server.Dispose();
    }
  }
}
