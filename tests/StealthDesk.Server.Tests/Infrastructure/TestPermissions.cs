using StealthDesk.Contracts.Permissions;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>Assigns permissions directly, without the presets registration seeds.</summary>
public static class TestPermissions
{
  /// <param name="scopeId">The tenant, device or user group; ignored at server scope.</param>
  public static Task AssignAsync(
    ServerHost server,
    UserRecord user,
    string permission,
    PermissionScopeKind scope,
    Guid? scopeId = null,
    PermissionEffect effect = PermissionEffect.Allow) => server.WithDbAsync(async db =>
  {
    var serverWide = scope == PermissionScopeKind.Server;
    db.PermissionAssignments.Add(new PermissionAssignmentRecord
    {
      PrincipalKind = PermissionPrincipalKind.User,
      PrincipalId = user.Id,
      Permission = permission,
      Effect = effect,
      ScopeKind = scope,
      ScopeId = serverWide ? null : scopeId,
      OwningTenantId = serverWide ? null : user.TenantId,
    });
    return await db.SaveChangesAsync();
  });

  /// <summary>Every device of the user's tenant: what someone watching the dashboard holds.</summary>
  public static Task AllowTenantDevicesAsync(ServerHost server, UserRecord user) =>
    AssignAsync(server, user, PermissionNames.DeviceRead, PermissionScopeKind.Tenant, user.TenantId);
}
