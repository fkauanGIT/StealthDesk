using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Accounts;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Server.Tests;

/// <summary>Permissions in the running server: presets on registration, policies, groups, and changes taking effect at once.</summary>
public class PermissionTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task FirstUser_ReceivesEveryPresetOnce_AtTheRightScopes()
  {
    using var server = ServerHost.InMemory();
    using var client = TestAccounts.Client(server);
    await RegisterAsync(client, "first@example.com");

    var held = await HeldAsync(server, "first@example.com");

    var expected = PermissionPresets.FirstUser.Concat(PermissionPresets.TenantCreator).Concat(PermissionPresets.Baseline)
      .SelectMany(PermissionPresets.PermissionsOf)
      .Distinct()
      .Select(x => (x, PermissionCatalog.Find(x)!.PresetScope))
      .ToHashSet();
    Assert.Equal(expected.Count, held.Count);
    Assert.Equal(expected, held.ToHashSet());
    Assert.Contains((PermissionNames.ServerTenantsRead, PermissionScopeKind.Server), held);
    Assert.Contains((PermissionNames.DeviceRead, PermissionScopeKind.Tenant), held);
  }

  [Fact]
  public async Task LaterUser_AdministersOnlyTheirOwnTenant()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    using var client = TestAccounts.Client(server);
    await RegisterAsync(client, "first@example.com");
    await RegisterAsync(client, "second@example.com");

    var held = await HeldAsync(server, "second@example.com");

    Assert.DoesNotContain(held, x => x.ScopeKind == PermissionScopeKind.Server);
    Assert.Contains((PermissionNames.TenantPermissionsWrite, PermissionScopeKind.Tenant), held);
    Assert.Contains((PermissionNames.PersonalAccessTokenSelfWrite, PermissionScopeKind.Tenant), held);
  }

  [Fact]
  public async Task SignedInUser_CarriesTheirPrincipal()
  {
    using var server = ServerHost.InMemory();
    var user = await TestAccounts.CreateUserAsync(server, "ana@example.com");

    var principal = Principal.From(await PrincipalOfAsync(server, "ana@example.com"));

    Assert.Equal(new Principal(PermissionPrincipalKind.User, user.Id, user.TenantId), principal);
  }

  [Fact]
  public void Principal_WithoutItsClaimsOrTenant_IsNobody()
  {
    var id = Guid.NewGuid().ToString();
    var noKind = new ClaimsPrincipal(new ClaimsIdentity([new Claim(Principal.IdClaim, id)], "test"));
    var noTenant = new ClaimsPrincipal(new ClaimsIdentity([new Claim(Principal.KindClaim, "User"), new Claim(Principal.IdClaim, id)], "test"));

    Assert.Null(Principal.From(noKind));
    Assert.Null(Principal.From(noTenant));
  }

  [Fact]
  public async Task Policies_FollowTheAdministratorsAssignments()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    using var client = TestAccounts.Client(server);
    await RegisterAsync(client, "admin@example.com");
    await RegisterAsync(client, "other@example.com");
    var admin = await PrincipalOfAsync(server, "admin@example.com");
    var other = await PrincipalOfAsync(server, "other@example.com");
    var ownDevice = await DeviceInAsync(server, admin.GetTenantId());
    var foreignDevice = await DeviceInAsync(server, other.GetTenantId());

    Assert.True(await AllowedAsync(server, admin, PermissionNames.TenantUsersRead));
    Assert.True(await AllowedAsync(server, admin, PermissionNames.ServerTenantsRead));
    Assert.True(await AllowedAsync(server, admin, PermissionNames.DeviceRead, ownDevice));
    Assert.True(await AllowedAsync(server, admin, PermissionNames.DeviceRead));
    Assert.False(await AllowedAsync(server, admin, PermissionNames.DeviceRead, foreignDevice));
    Assert.False(await AllowedAsync(server, other, PermissionNames.ServerTenantsRead));
  }

  [Fact]
  public async Task DirectDeny_OverridesAnAllowFromTheUsersGroup()
  {
    using var server = ServerHost.InMemory();
    var member = await TestAccounts.CreateUserAsync(server, "member@example.com");
    var principal = await PrincipalOfAsync(server, "member@example.com");
    var device = await DeviceInAsync(server, member.TenantId);
    var otherDevice = await DeviceInAsync(server, member.TenantId);
    var group = await GroupWithAsync(server, member);
    await AssignAsync(server, PermissionPrincipalKind.UserGroup, group, PermissionScopeKind.Tenant, member.TenantId, member.TenantId);

    var allowedByGroup = await AllowedAsync(server, principal, PermissionNames.DeviceRead, device);
    await AssignAsync(server, PermissionPrincipalKind.User, member.Id, PermissionScopeKind.Device, device.Id, member.TenantId, PermissionEffect.Deny);

    Assert.True(allowedByGroup);
    Assert.False(await AllowedAsync(server, principal, PermissionNames.DeviceRead, device));
    Assert.True(await AllowedAsync(server, principal, PermissionNames.DeviceRead, otherDevice));
  }

  [Fact]
  public async Task RemovingAPreset_RemovesTheAccessOnTheNextRequest()
  {
    using var server = ServerHost.InMemory();
    using var client = TestAccounts.Client(server);
    await RegisterAsync(client, "admin@example.com");
    var cookie = await TestAccounts.SignInForCookieAsync(server, "admin@example.com");
    var admin = await PrincipalOfAsync(server, "admin@example.com");
    var before = await MeAsync(client, cookie);

    // Take the Tenant Administrator preset away, as a page will once permissions can be managed.
    var preset = PermissionPresets.PermissionsOf(PermissionPresets.TenantAdministrator).ToHashSet();
    await server.WithDbAsync(async db =>
    {
      db.PermissionAssignments.RemoveRange(db.PermissionAssignments.Where(x => preset.Contains(x.Permission)));
      return await db.SaveChangesAsync();
    });
    var after = await MeAsync(client, cookie);

    Assert.True(before.IsTenantAdministrator);
    Assert.False(after.IsTenantAdministrator);
    Assert.True(after.IsServerAdministrator);
    Assert.False(await AllowedAsync(server, admin, PermissionNames.TenantUsersRead));
  }

  [Fact]
  public async Task Seeder_GrantsEachPermissionOnce_AtTheServerOnlyWhenItLivesThere()
  {
    using var server = ServerHost.InMemory();
    var user = await TestAccounts.CreateUserAsync(server, "ana@example.com");

    await SeedAsync(server, user, PermissionPresets.Baseline);
    var baseline = await HeldAsync(server, "ana@example.com");
    await SeedAsync(server, user, [PermissionPresets.ServerAdministrator, PermissionPresets.SelfService]);
    var rows = await server.WithDbAsync(db => db.PermissionAssignments.Where(x => x.PrincipalId == user.Id).ToListAsync());

    Assert.Equal(
      [(PermissionNames.PersonalAccessTokenSelfRead, PermissionScopeKind.Tenant), (PermissionNames.PersonalAccessTokenSelfWrite, PermissionScopeKind.Tenant)],
      baseline.Order());
    Assert.Equal(rows.Count, rows.Select(x => (x.Permission, x.ScopeKind)).Distinct().Count());
    var serverRow = Assert.Single(rows, x => x.Permission == PermissionNames.ServerTenantsRead);
    Assert.Equal((PermissionScopeKind.Server, (Guid?)null, (Guid?)null), (serverRow.ScopeKind, serverRow.ScopeId, serverRow.OwningTenantId));
    var tenantRow = Assert.Single(rows, x => x.Permission == PermissionNames.TenantPermissionsRead);
    Assert.Equal((PermissionScopeKind.Tenant, (Guid?)user.TenantId, (Guid?)user.TenantId), (tenantRow.ScopeKind, tenantRow.ScopeId, tenantRow.OwningTenantId));
  }

  [Fact]
  public async Task DisabledAssignment_GrantsNothing()
  {
    using var server = ServerHost.InMemory();
    using var client = TestAccounts.Client(server);
    await RegisterAsync(client, "admin@example.com");
    var admin = await PrincipalOfAsync(server, "admin@example.com");

    await server.WithDbAsync(async db =>
    {
      await db.PermissionAssignments.Where(x => x.Permission == PermissionNames.TenantUsersRead).ForEachAsync(x => x.IsEnabled = false);
      return await db.SaveChangesAsync();
    });

    Assert.False(await AllowedAsync(server, admin, PermissionNames.TenantUsersRead));
    Assert.True(await AllowedAsync(server, admin, PermissionNames.TenantUsersWrite));
  }

  private static async Task RegisterAsync(HttpClient client, string email)
  {
    var response = await client.PostAsJsonAsync($"{Routes.Auth}/register", new { email, password = TestAccounts.Password }, Cancel);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
  }

  private static async Task SeedAsync(ServerHost server, UserRecord user, IEnumerable<string> presets)
  {
    await using var scope = server.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<PermissionSeeder>().SeedAsync(user.Id, user.TenantId, presets, Cancel);
  }

  private static async Task<CurrentUser> MeAsync(HttpClient client, string cookie)
  {
    var response = await client.SendAsync(TestAccounts.WithCookie(HttpMethod.Get, Routes.CurrentUser, cookie), Cancel);
    return (await response.Content.ReadFromJsonAsync<CurrentUser>(Cancel))!;
  }

  private static Task<List<(string Permission, PermissionScopeKind ScopeKind)>> HeldAsync(ServerHost server, string email) =>
    server.WithDbAsync(async db =>
    {
      var userId = await db.Users.Where(x => x.Email == email).Select(x => x.Id).SingleAsync();
      var rows = await db.PermissionAssignments.Where(x => x.PrincipalId == userId).ToListAsync();
      return rows.Select(x => (x.Permission, x.ScopeKind)).ToList();
    });

  /// <summary>The claims a sign-in would give the user.</summary>
  private static async Task<ClaimsPrincipal> PrincipalOfAsync(ServerHost server, string email)
  {
    await using var scope = server.Services.CreateAsyncScope();
    var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<UserRecord>>();
    return await signIn.CreateUserPrincipalAsync((await signIn.UserManager.FindByEmailAsync(email))!);
  }

  private static async Task<bool> AllowedAsync(ServerHost server, ClaimsPrincipal user, string permission, object? resource = null)
  {
    await using var scope = server.Services.CreateAsyncScope();
    var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
    return (await authorization.AuthorizeAsync(user, resource, PermissionPolicies.For(permission))).Succeeded;
  }

  private static Task<DeviceRecord> DeviceInAsync(ServerHost server, Guid? tenantId) => server.WithDbAsync(async db =>
  {
    var device = new DeviceRecord { Id = Guid.NewGuid(), TenantId = tenantId!.Value, Name = "PC" };
    db.Devices.Add(device);
    await db.SaveChangesAsync();
    return device;
  });

  private static Task<Guid> GroupWithAsync(ServerHost server, UserRecord member) => server.WithDbAsync(async db =>
  {
    var group = new UserGroupRecord { Id = Guid.NewGuid(), TenantId = member.TenantId, Name = "Technicians" };
    group.Members.Add(new UserGroupMemberRecord { UserId = member.Id });
    db.UserGroups.Add(group);
    await db.SaveChangesAsync();
    return group.Id;
  });

  private static Task AssignAsync(
    ServerHost server,
    PermissionPrincipalKind kind,
    Guid principalId,
    PermissionScopeKind scope,
    Guid scopeId,
    Guid tenantId,
    PermissionEffect effect = PermissionEffect.Allow) => server.WithDbAsync(async db =>
  {
    db.PermissionAssignments.Add(new PermissionAssignmentRecord
    {
      PrincipalKind = kind,
      PrincipalId = principalId,
      Permission = PermissionNames.DeviceRead,
      Effect = effect,
      ScopeKind = scope,
      ScopeId = scopeId,
      OwningTenantId = tenantId,
    });
    return await db.SaveChangesAsync();
  });
}
