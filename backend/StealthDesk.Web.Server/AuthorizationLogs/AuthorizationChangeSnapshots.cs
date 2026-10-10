using StealthDesk.Contracts.Permissions;

namespace StealthDesk.Web.Server.AuthorizationLogs;

/// <summary>
/// Presets granted in one go, e.g. at registration: one entry for the whole grant instead of one per permission.
/// </summary>
public sealed record PermissionSeedSummary(int Count, IReadOnlyList<string> Presets);

/// <summary>One assignment as it was, e.g. before it was removed.</summary>
public sealed record PermissionAssignmentSnapshot(string Permission, PermissionEffect Effect, PermissionScopeKind ScopeKind, Guid? ScopeId)
{
  public static PermissionAssignmentSnapshot Of(PermissionAssignmentRecord assignment) =>
    new(assignment.Permission, assignment.Effect, assignment.ScopeKind, assignment.ScopeId);
}
