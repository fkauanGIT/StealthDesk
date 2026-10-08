using StealthDesk.Contracts.Permissions;

namespace StealthDesk.Web.Server.Permissions;

/// <summary>
/// Grants presets to a new user, as the server's own act: no one is checked for the right to grant them. Each
/// permission is granted once, at the broadest scope that stays in the user's tenant (server scope for permissions
/// that only exist there).
/// </summary>
public sealed class PermissionSeeder(StealthDeskDb db)
{
  public async Task SeedAsync(Guid userId, Guid tenantId, IEnumerable<string> presets, CancellationToken cancellationToken = default)
  {
    var held = await db.PermissionAssignments
      .IgnoreQueryFilters()
      .Where(x => x.PrincipalKind == PermissionPrincipalKind.User && x.PrincipalId == userId && x.Effect == PermissionEffect.Allow)
      .Select(x => new { x.Permission, x.ScopeKind })
      .ToListAsync(cancellationToken);

    var granted = held.Select(x => (x.Permission, x.ScopeKind)).ToHashSet();
    foreach (var permission in presets.SelectMany(PermissionPresets.PermissionsOf))
    {
      var scope = PermissionCatalog.Find(permission)?.PresetScope
        ?? throw new InvalidOperationException($"Preset permission '{permission}' isn't in the catalog.");

      if (granted.Add((permission, scope)))
      {
        db.PermissionAssignments.Add(PermissionAssignmentRecord.SystemGrant(PermissionPrincipalKind.User, userId, permission, scope, tenantId));
      }
    }

    await db.SaveChangesAsync(cancellationToken);
  }
}
