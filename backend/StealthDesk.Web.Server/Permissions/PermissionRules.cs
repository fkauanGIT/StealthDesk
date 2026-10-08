using StealthDesk.Contracts.Permissions;

namespace StealthDesk.Web.Server.Permissions;

/// <summary>Where a rule came from. Lower wins when two rules of the same effect could decide.</summary>
public enum RuleSource
{
  Direct = 0,
  UserGroup = 1,
}

/// <summary>An assignment ready to be evaluated, with where it came from.</summary>
public sealed record PermissionRule(
  string Permission,
  PermissionEffect Effect,
  PermissionScopeKind ScopeKind,
  Guid? ScopeId,
  Guid? OwningTenantId,
  RuleSource Source)
{
  public static PermissionRule From(PermissionAssignmentRecord assignment, RuleSource source) =>
    new(assignment.Permission, assignment.Effect, assignment.ScopeKind, assignment.ScopeId, assignment.OwningTenantId, source);
}

/// <summary>
/// The decision itself, with no database: unknown permissions and missing rules are denied, a deny that reaches
/// the resource wins over any allow, and an allow must be at a scope the permission can be granted at.
/// </summary>
public static class PermissionRules
{
  /// <summary>Turns stored assignments into rules, dropping those the principal can't hold.</summary>
  /// <param name="tenantId">The principal's tenant; null for a server-wide principal, which keeps every row.</param>
  public static IEnumerable<PermissionRule> Applicable(IEnumerable<PermissionAssignmentRecord> assignments, Guid? tenantId, RuleSource source) =>
    assignments
      .Where(x => x.IsEnabled && BelongsTo(x, tenantId) && !OverreachesTenant(x, tenantId))
      .Select(x => PermissionRule.From(x, source));

  public static PermissionDecision Evaluate(IReadOnlyCollection<PermissionRule> rules, string permission, Resource resource)
  {
    if (PermissionCatalog.Find(permission) is not { } info)
    {
      return PermissionDecision.Deny($"Unknown permission '{permission}'.");
    }

    // An allow at a scope the permission can't be granted at grants nothing. A deny counts wherever it is:
    // ignoring one would widen access.
    var matching = rules
      .Where(x => x.Permission == permission
        && (x.Effect == PermissionEffect.Deny || info.Scopes.Contains(x.ScopeKind))
        && Reaches(x, resource))
      .ToList();

    var deny = Decisive(matching, PermissionEffect.Deny);
    if (deny is not null)
    {
      return PermissionDecision.Deny($"Denied by a {Describe(deny.Source)} assignment at {deny.ScopeKind} scope.");
    }

    var allow = Decisive(matching, PermissionEffect.Allow);
    return allow is null
      ? PermissionDecision.Deny("No assignment grants it.")
      : PermissionDecision.Allow($"Allowed by a {Describe(allow.Source)} assignment at {allow.ScopeKind} scope.");
  }

  /// <summary>Whether the rule's scope covers the resource: the server covers everything, a tenant all it holds.</summary>
  public static bool Reaches(PermissionRule rule, Resource resource) => rule.ScopeKind switch
  {
    PermissionScopeKind.Server => true,
    PermissionScopeKind.Tenant => resource.TenantId is { } tenantId && rule.ScopeId == tenantId,
    PermissionScopeKind.Device or PermissionScopeKind.UserGroup => rule.ScopeKind == resource.Kind && rule.ScopeId == resource.Id,
    _ => false,
  };

  // The closest source, then the narrowest scope: the reason given is the most specific rule that decided.
  private static PermissionRule? Decisive(IEnumerable<PermissionRule> rules, PermissionEffect effect) =>
    rules
      .Where(x => x.Effect == effect)
      .OrderBy(x => x.Source)
      .ThenBy(x => Breadth(x.ScopeKind))
      .FirstOrDefault();

  private static int Breadth(PermissionScopeKind kind) => kind switch
  {
    PermissionScopeKind.Device => 0,
    PermissionScopeKind.UserGroup => 1,
    PermissionScopeKind.Tenant => 2,
    PermissionScopeKind.Server => 3,
    _ => 4,
  };

  private static string Describe(RuleSource source) => source == RuleSource.UserGroup ? "user group" : "direct";

  // A tenant's principal holds only rows of its own tenant, and server rows (which no tenant owns).
  private static bool BelongsTo(PermissionAssignmentRecord assignment, Guid? tenantId) =>
    tenantId is null || assignment.OwningTenantId is null || assignment.OwningTenantId == tenantId;

  // A server-scope allow of something grantable in a tenant would reach every tenant; a tenant's principal can't
  // hold one. A server-scope deny is kept: dropping it would widen access.
  private static bool OverreachesTenant(PermissionAssignmentRecord assignment, Guid? tenantId) =>
    tenantId is not null
    && assignment.ScopeKind == PermissionScopeKind.Server
    && assignment.Effect == PermissionEffect.Allow
    && PermissionCatalog.Find(assignment.Permission) is { AllowsTenantScope: true };
}
