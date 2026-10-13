namespace StealthDesk.Contracts.Permissions;

/// <summary>One permission granted or denied to a principal at a scope.</summary>
public sealed record PermissionAssignment(
  Guid Id,
  PermissionPrincipalKind PrincipalKind,
  Guid PrincipalId,
  string Permission,
  PermissionEffect Effect,
  PermissionScopeKind ScopeKind,
  Guid? ScopeId,
  string? Notes,
  bool IsEnabled,
  DateTimeOffset CreatedAt);

public sealed class PermissionAssignmentList
{
  public IReadOnlyList<PermissionAssignment> Items { get; init; } = [];
}

/// <param name="ScopeId">The device or user group; ignored at server and tenant scope.</param>
public sealed record CreatePermissionAssignmentRequest(
  PermissionPrincipalKind PrincipalKind,
  Guid PrincipalId,
  string Permission,
  PermissionEffect Effect,
  PermissionScopeKind ScopeKind,
  Guid? ScopeId = null,
  string? Notes = null,
  bool IsEnabled = true)
{
  public const int NotesMax = 500;
}

/// <summary>Changes what an assignment grants; its principal stays.</summary>
public sealed record UpdatePermissionAssignmentRequest(
  string Permission,
  PermissionEffect Effect,
  PermissionScopeKind ScopeKind,
  Guid? ScopeId = null,
  string? Notes = null,
  bool IsEnabled = true);

public sealed record DeletePermissionAssignmentsRequest(IReadOnlyList<Guid> Ids);

/// <param name="FailureIds">Not found, or not visible to the caller.</param>
public sealed record DeletePermissionAssignmentsResult(IReadOnlyList<Guid> SuccessIds, IReadOnlyList<Guid> FailureIds);

/// <param name="ReplaceExisting">
/// Replace the principal's assignments at the scopes the presets grant at, instead of adding what's missing.
/// </param>
public sealed record ApplyPermissionPresetsRequest(
  PermissionPrincipalKind PrincipalKind,
  Guid PrincipalId,
  IReadOnlyList<string> Presets,
  bool ReplaceExisting = false);

/// <summary>A permission as the pages that grant it describe it.</summary>
/// <param name="Scopes">Where it can be granted.</param>
public sealed record PermissionCatalogEntry(string Name, string Category, string DisplayName, string Description, IReadOnlyList<PermissionScopeKind> Scopes);

public sealed class PermissionCatalogList
{
  public IReadOnlyList<PermissionCatalogEntry> Items { get; init; } = [];
}

public sealed record PermissionPresetEntry(string Name, IReadOnlyList<string> Permissions);

public sealed class PermissionPresetList
{
  public IReadOnlyList<PermissionPresetEntry> Items { get; init; } = [];
}

/// <summary>Whether a principal holds a permission on a resource, and the rule that decided it.</summary>
public sealed record EffectivePermission(bool IsAllowed, string Reason);
