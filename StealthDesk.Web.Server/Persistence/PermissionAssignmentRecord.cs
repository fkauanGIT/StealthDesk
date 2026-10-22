using StealthDesk.Contracts.Permissions;

namespace StealthDesk.Web.Server.Persistence;

/// <summary>
/// One permission granted or denied to one principal at one scope: "this user may read devices in this tenant", or
/// "this group may not read this device".
/// </summary>
public class PermissionAssignmentRecord
{
  public const int PermissionMax = 150;
  public const int NotesMax = 500;
  public const int CreatedByKindMax = 50;

  /// <summary>Who created an assignment when nobody did: registration, presets for a new tenant, migrations.</summary>
  public const string System = "System";

  public Guid Id { get; set; }

  public PermissionPrincipalKind PrincipalKind { get; set; }
  public Guid PrincipalId { get; set; }

  public string Permission { get; set; } = string.Empty;
  public PermissionEffect Effect { get; set; }

  public PermissionScopeKind ScopeKind { get; set; }

  /// <summary>The tenant, device or group the scope names; none at server scope.</summary>
  public Guid? ScopeId { get; set; }

  /// <summary>The tenant the assignment belongs to; none at server scope, which no tenant owns.</summary>
  public Guid? OwningTenantId { get; set; }

  /// <summary>A disabled assignment is kept but grants and denies nothing.</summary>
  public bool IsEnabled { get; set; } = true;

  public string? Notes { get; set; }

  /// <summary><see cref="System"/>, or the kind of principal that created it.</summary>
  public string CreatedByKind { get; set; } = System;
  public Guid? CreatedById { get; set; }
  public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

  /// <summary>An allow created by the server itself. Server-scope rows carry no scope ID and no owning tenant.</summary>
  public static PermissionAssignmentRecord SystemGrant(
    PermissionPrincipalKind principalKind,
    Guid principalId,
    string permission,
    PermissionScopeKind scopeKind,
    Guid tenantId) => new()
  {
    PrincipalKind = principalKind,
    PrincipalId = principalId,
    Permission = permission,
    Effect = PermissionEffect.Allow,
    ScopeKind = scopeKind,
    ScopeId = scopeKind == PermissionScopeKind.Server ? null : tenantId,
    OwningTenantId = scopeKind == PermissionScopeKind.Server ? null : tenantId,
  };
}
