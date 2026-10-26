using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>
/// Upgrading older databases: the v0.3 administrator markers become permission assignments, and back; the terminal
/// reaches those who received Device Superuser from the system before it included it.
/// </summary>
public class PermissionMigrationTests
{
  private const string BeforePermissions = "20261003020144_AddUsers";
  private const string BeforeTerminal = "20261016110021_AddDeviceGroups";
  private const string ServerMarker = "stealthdesk:server_admin";
  private const string TenantMarker = "stealthdesk:tenant_admin";

  [Fact]
  public async Task Markers_BecomeAssignments_AndComeBackWhenUndone()
  {
    using var server = await ServerHost.OnPostgresAsync();
    // Accounts are made with today's schema, then taken back to before permissions to be marked the old way.
    var admin = await CreateUserAsync(server, "admin@example.com");
    var member = await CreateUserAsync(server, "member@example.com");
    await MigrateAsync(server, BeforePermissions);
    await MarkAsync(server, admin, ServerMarker, TenantMarker);

    await MigrateAsync(server, null);
    var adminRows = await RowsAsync(server, admin.Id);
    var memberRows = await RowsAsync(server, member.Id);
    var markersAfterUpgrade = await MarkersAsync(server);

    Assert.Contains((PermissionNames.ServerPermissionsWrite, PermissionScopeKind.Server, (Guid?)null), adminRows);
    Assert.Contains((PermissionNames.TenantPermissionsWrite, PermissionScopeKind.Tenant, (Guid?)admin.TenantId), adminRows);
    Assert.Contains((PermissionNames.DeviceRead, PermissionScopeKind.Tenant, (Guid?)admin.TenantId), adminRows);
    Assert.Equal(adminRows.Count, adminRows.Distinct().Count());
    Assert.Equal(
      [PermissionNames.PersonalAccessTokenSelfRead, PermissionNames.PersonalAccessTokenSelfWrite],
      memberRows.Select(x => x.Permission).Order());
    Assert.Empty(markersAfterUpgrade);

    // The migration's own copy of the presets matches today's presets.
    var expected = PermissionPresets.FirstUser.Concat(PermissionPresets.TenantCreator).Concat(PermissionPresets.Baseline)
      .SelectMany(PermissionPresets.PermissionsOf).Distinct().Order();
    Assert.Equal(expected, adminRows.Select(x => x.Permission).Order());

    await MigrateAsync(server, BeforePermissions);
    var restored = await MarkersAsync(server);

    Assert.Equal([(admin.Id, ServerMarker), (admin.Id, TenantMarker)], restored.Order());
  }

  [Fact]
  public async Task DeviceReadTheSystemGave_BringsTheTerminal_AndUndoingTakesItAway()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var tenant = await TestTenants.CreateAsync(server, "Tenant");
    var creator = await TestAccounts.CreateUserAsync(server, "creator@example.com", tenant);
    var operatorUser = await TestAccounts.CreateUserAsync(server, "operator@example.com", tenant);
    var disabled = await TestAccounts.CreateUserAsync(server, "disabled@example.com", tenant);
    var oneDevice = await TestAccounts.CreateUserAsync(server, "one-device@example.com", tenant);
    var denied = await TestAccounts.CreateUserAsync(server, "denied@example.com", tenant);
    var holder = await TestAccounts.CreateUserAsync(server, "holder@example.com", tenant);
    var group = Guid.NewGuid();
    await MigrateAsync(server, BeforeTerminal);

    // As the version before left them: device.read given by the system, by a person, switched off, on one device,
    // denied; one who already holds the terminal; and a user group.
    await server.WithDbAsync(db =>
    {
      db.PermissionAssignments.AddRange(
        Assignment(PermissionPrincipalKind.User, creator.Id, PermissionNames.DeviceRead, tenant),
        Assignment(PermissionPrincipalKind.User, operatorUser.Id, PermissionNames.DeviceRead, tenant, createdBy: "User"),
        Assignment(PermissionPrincipalKind.User, disabled.Id, PermissionNames.DeviceRead, tenant, enabled: false),
        Assignment(PermissionPrincipalKind.User, oneDevice.Id, PermissionNames.DeviceRead, tenant, scope: PermissionScopeKind.Device, scopeId: Guid.NewGuid()),
        Assignment(PermissionPrincipalKind.User, denied.Id, PermissionNames.DeviceRead, tenant, effect: PermissionEffect.Deny),
        Assignment(PermissionPrincipalKind.User, holder.Id, PermissionNames.DeviceRead, tenant),
        Assignment(PermissionPrincipalKind.User, holder.Id, PermissionNames.DeviceTerminalUse, tenant, createdBy: "User"),
        Assignment(PermissionPrincipalKind.UserGroup, group, PermissionNames.DeviceRead, tenant));
      return db.SaveChangesAsync();
    });

    await MigrateAsync(server, null);
    var terminal = await TerminalRowsAsync(server);

    Assert.Equal(
      new[] { creator.Id, group, holder.Id }.Order(),
      terminal.Select(x => x.PrincipalId).Order());
    Assert.All(terminal.Where(x => x.PrincipalId != holder.Id), x =>
    {
      Assert.Equal(PermissionEffect.Allow, x.Effect);
      Assert.Equal(PermissionScopeKind.Tenant, x.ScopeKind);
      Assert.Equal(tenant, x.ScopeId);
      Assert.Equal(tenant, x.OwningTenantId);
      Assert.True(x.IsEnabled);
      Assert.Equal(PermissionAssignmentRecord.System, x.CreatedByKind);
    });
    Assert.Equal(PermissionPrincipalKind.UserGroup, Assert.Single(terminal, x => x.PrincipalId == group).PrincipalKind);
    Assert.Equal("User", Assert.Single(terminal, x => x.PrincipalId == holder.Id).CreatedByKind);

    await MigrateAsync(server, BeforeTerminal);

    Assert.Empty(await TerminalRowsAsync(server));
    Assert.Equal(7, await server.WithDbAsync(db => db.PermissionAssignments.CountAsync(x => x.Permission == PermissionNames.DeviceRead && x.OwningTenantId == tenant)));
  }

  private static PermissionAssignmentRecord Assignment(
    PermissionPrincipalKind kind,
    Guid principal,
    string permission,
    Guid tenant,
    string createdBy = PermissionAssignmentRecord.System,
    bool enabled = true,
    PermissionScopeKind scope = PermissionScopeKind.Tenant,
    Guid? scopeId = null,
    PermissionEffect effect = PermissionEffect.Allow) => new()
  {
    PrincipalKind = kind,
    PrincipalId = principal,
    Permission = permission,
    Effect = effect,
    ScopeKind = scope,
    ScopeId = scopeId ?? tenant,
    OwningTenantId = tenant,
    IsEnabled = enabled,
    CreatedByKind = createdBy,
  };

  private static Task<List<PermissionAssignmentRecord>> TerminalRowsAsync(ServerHost server) =>
    server.WithDbAsync(db => db.PermissionAssignments.AsNoTracking().Where(x => x.Permission == PermissionNames.DeviceTerminalUse).ToListAsync());

  private static async Task MigrateAsync(ServerHost server, string? target)
  {
    await using var scope = server.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<StealthDeskDb>();
    await db.GetService<IMigrator>().MigrateAsync(target, TestContext.Current.CancellationToken);
  }

  private static async Task<UserRecord> CreateUserAsync(ServerHost server, string email) =>
    await TestAccounts.CreateUserAsync(server, email, await TestTenants.CreateAsync(server, email));

  // Straight into the claims table: the user manager reads every column users have today, which an older schema lacks.
  private static Task MarkAsync(ServerHost server, UserRecord user, params string[] markers) => server.WithDbAsync(db =>
  {
    db.UserClaims.AddRange(markers.Select(x => new IdentityUserClaim<Guid> { UserId = user.Id, ClaimType = x, ClaimValue = "true" }));
    return db.SaveChangesAsync();
  });

  private static Task<List<(string Permission, PermissionScopeKind ScopeKind, Guid? ScopeId)>> RowsAsync(ServerHost server, Guid userId) =>
    server.WithDbAsync(async db =>
    {
      var rows = await db.PermissionAssignments.Where(x => x.PrincipalId == userId).ToListAsync();
      Assert.All(rows, x =>
      {
        Assert.Equal(PermissionPrincipalKind.User, x.PrincipalKind);
        Assert.Equal(PermissionEffect.Allow, x.Effect);
        Assert.Equal(x.ScopeId, x.OwningTenantId);
        Assert.Equal(PermissionAssignmentRecord.System, x.CreatedByKind);
      });
      return rows.Select(x => (x.Permission, x.ScopeKind, x.ScopeId)).ToList();
    });

  private static Task<List<(Guid UserId, string Type)>> MarkersAsync(ServerHost server) => server.WithDbAsync(async db =>
  {
    var claims = await db.UserClaims.Where(x => x.ClaimType == ServerMarker || x.ClaimType == TenantMarker).ToListAsync();
    return claims.Select(x => (x.UserId, x.ClaimType!)).ToList();
  });
}
