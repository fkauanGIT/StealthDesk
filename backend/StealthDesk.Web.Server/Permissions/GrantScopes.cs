using StealthDesk.Contracts.Permissions;

namespace StealthDesk.Web.Server.Permissions;

/// <summary>Whether a permission can be granted where it's asked to be, to a user, a group or a token.</summary>
public static class GrantScopes
{
  /// <summary>Why the grant makes no sense, or null when it does.</summary>
  public static async Task<string?> RefusalAsync(
    StealthDeskDb db,
    string permission,
    PermissionEffect effect,
    PermissionScopeKind scope,
    Guid? scopeId,
    Guid tenant,
    CancellationToken cancellationToken)
  {
    if (PermissionCatalog.Find(permission) is not { } info)
    {
      return $"Unknown permission: {permission}.";
    }

    if (!info.Scopes.Contains(scope))
    {
      return $"'{info.DisplayName}' can't be assigned at {scope} scope.";
    }

    // A server-wide allow of something granted inside tenants would reach every tenant; only principals that
    // belong to no tenant may hold one, and users, groups and their tokens always belong to one.
    if (effect == PermissionEffect.Allow && scope == PermissionScopeKind.Server && info.AllowsTenantScope)
    {
      return "Server-scoped allows of tenant permissions can't be given to users, user groups or their tokens.";
    }

    return scope switch
    {
      PermissionScopeKind.Device when scopeId is not { } device || !await db.Devices.IgnoreQueryFilters().AnyAsync(x => x.Id == device && x.TenantId == tenant, cancellationToken)
        => "Choose a device of this tenant.",
      PermissionScopeKind.UserGroup when scopeId is not { } group || !await db.UserGroups.IgnoreQueryFilters().AnyAsync(x => x.Id == group && x.TenantId == tenant, cancellationToken)
        => "Choose a user group of this tenant.",
      _ => null,
    };
  }

  /// <summary>
  /// A token may never be granted what its owner can't do: an allow must be one the owner holds where it's granted.
  /// A deny takes access away, so it needs nothing from the owner.
  /// </summary>
  public static async Task<string?> BeyondOwnerAsync(
    IPermissionEvaluator evaluator,
    Guid ownerId,
    Guid tenant,
    string permission,
    PermissionEffect effect,
    PermissionScopeKind scope,
    Guid? scopeId,
    CancellationToken cancellationToken)
  {
    if (effect == PermissionEffect.Deny)
    {
      return null;
    }

    Resource resource = scope switch
    {
      PermissionScopeKind.Server => Resource.Server,
      PermissionScopeKind.Device => Resource.Device(scopeId!.Value, tenant),
      PermissionScopeKind.UserGroup => Resource.UserGroup(scopeId!.Value, tenant),
      _ => Resource.Tenant(tenant),
    };
    var owner = new Principal(PermissionPrincipalKind.User, ownerId, tenant);
    return (await evaluator.EvaluateAsync(owner, permission, resource, cancellationToken)).Allowed
      ? null
      : $"The permission '{permission}' at {scope} scope is beyond what the token's owner can do.";
  }
}
