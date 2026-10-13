using StealthDesk.Contracts.Permissions;

namespace StealthDesk.Web.Client.Permissions;

/// <summary>What the assignment form holds, to create an assignment or change one.</summary>
public sealed record AssignmentDraft(string Permission, PermissionEffect Effect, PermissionScopeKind ScopeKind, Guid? ScopeId, string? Notes, bool IsEnabled);
