using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.AuthorizationLogs;

namespace StealthDesk.Web.Server.Permissions;

/// <summary>
/// Grants presets to a new user, as the server's own act: no one is checked for the right to grant them. Each
/// permission is granted once, at the broadest scope that stays in the user's tenant (server scope for permissions
/// that only exist there). The grant is logged as one entry, saved with it.
/// </summary>
public sealed class PermissionSeeder(StealthDeskDb db, IAuthorizationChangeFactory changes)
{
  public async Task SeedAsync(Guid userId, Guid tenantId, IEnumerable<string> presets, CancellationToken cancellationToken = default)
  {
    var held = await db.PermissionAssignments
      .IgnoreQueryFilters()
      .Where(x => x.PrincipalKind == PermissionPrincipalKind.User && x.PrincipalId == userId && x.Effect == PermissionEffect.Allow)
      .Select(x => new { x.Permission, x.ScopeKind })
      .ToListAsync(cancellationToken);

    var granted = held.Select(x => (x.Permission, x.ScopeKind)).ToHashSet();
    var presetNames = presets.Distinct().ToList();
    var added = 0;
    foreach (var permission in presetNames.SelectMany(PermissionPresets.PermissionsOf))
    {
      var scope = PermissionCatalog.Find(permission)?.PresetScope
        ?? throw new InvalidOperationException($"Preset permission '{permission}' isn't in the catalog.");

      if (granted.Add((permission, scope)))
      {
        db.PermissionAssignments.Add(PermissionAssignmentRecord.SystemGrant(PermissionPrincipalKind.User, userId, permission, scope, tenantId));
        added++;
      }
    }

    if (added > 0)
    {
      db.AuthorizationChanges.Add(changes.Create(
        AuthorizationChangeActions.PermissionAssignmentsSeeded,
        actor: null,
        AuthorizationChangeTargets.User,
        userId,
        tenantId,
        after: new PermissionSeedSummary(added, presetNames)));
    }

    await db.SaveChangesAsync(cancellationToken);
  }
}
