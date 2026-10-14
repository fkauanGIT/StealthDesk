using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.ServiceAccounts;

namespace StealthDesk.Web.Server.AuthorizationLogs;

/// <summary>
/// Presets granted in one go, e.g. at registration: one entry for the whole grant instead of one per permission.
/// </summary>
public sealed record PermissionSeedSummary(int Count, IReadOnlyList<string> Presets);

public sealed record UserGroupSnapshot(string Name, string? Description);

/// <summary>How many members were added or removed at once.</summary>
public sealed record UserGroupMembershipChange(int Count);

/// <summary>One assignment as it was, e.g. before it was changed or removed.</summary>
public sealed record PermissionAssignmentSnapshot(string Permission, PermissionEffect Effect, PermissionScopeKind ScopeKind, Guid? ScopeId, bool IsEnabled)
{
  public static PermissionAssignmentSnapshot Of(PermissionAssignmentRecord assignment) =>
    new(assignment.Permission, assignment.Effect, assignment.ScopeKind, assignment.ScopeId, assignment.IsEnabled);
}

public sealed record ServiceAccountSnapshot(string Name, ServiceAccountKind Kind, string? Description, bool IsEnabled, ServiceAccountAccessMode AccessMode)
{
  public static ServiceAccountSnapshot Of(ServiceAccountRecord account) =>
    new(account.Name, account.Kind, account.Description, account.IsEnabled, account.AccessMode);
}

public sealed record ServiceAccountCredentialSnapshot(string Name, Guid ServiceAccountId, DateTimeOffset? ExpiresAt)
{
  public static ServiceAccountCredentialSnapshot Of(ServiceAccountCredentialRecord credential) =>
    new(credential.Name, credential.ServiceAccountId, credential.ExpiresAt);
}
