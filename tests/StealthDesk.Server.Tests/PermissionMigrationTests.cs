using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>Upgrading a v0.3 database: the administrator markers become permission assignments, and back.</summary>
public class PermissionMigrationTests
{
  private const string BeforePermissions = "20261003020144_AddUsers";
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
