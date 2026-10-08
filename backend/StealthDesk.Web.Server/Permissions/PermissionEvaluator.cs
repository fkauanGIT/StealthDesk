using StealthDesk.Contracts.Permissions;

namespace StealthDesk.Web.Server.Permissions;

/// <summary>Answers "may this principal do this to that?" from the assignments stored now.</summary>
public interface IPermissionEvaluator
{
  Task<PermissionDecision> EvaluateAsync(Principal principal, string permission, Resource resource, CancellationToken cancellationToken = default);

  /// <summary>Several permissions on one resource, loading the principal's assignments once.</summary>
  Task<IReadOnlyDictionary<string, PermissionDecision>> EvaluateManyAsync(
    Principal principal,
    IReadOnlyCollection<string> permissions,
    Resource resource,
    CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the assignments on every call instead of caching them, so a change (a removed preset, a new deny) applies
/// to the very next request.
/// </summary>
public sealed class PermissionEvaluator(StealthDeskDb db) : IPermissionEvaluator
{
  public async Task<PermissionDecision> EvaluateAsync(Principal principal, string permission, Resource resource, CancellationToken cancellationToken = default) =>
    PermissionRules.Evaluate(await LoadRulesAsync(principal, cancellationToken), permission, resource);

  public async Task<IReadOnlyDictionary<string, PermissionDecision>> EvaluateManyAsync(
    Principal principal,
    IReadOnlyCollection<string> permissions,
    Resource resource,
    CancellationToken cancellationToken = default)
  {
    var rules = await LoadRulesAsync(principal, cancellationToken);
    return permissions
      .Distinct(StringComparer.Ordinal)
      .ToDictionary(x => x, x => PermissionRules.Evaluate(rules, x, resource), StringComparer.Ordinal);
  }

  // The principal's own assignments and, for a user, those of every group they are in. The tenant query filters
  // are bypassed: the rules apply the tenant boundary themselves, for any principal.
  private async Task<IReadOnlyCollection<PermissionRule>> LoadRulesAsync(Principal principal, CancellationToken cancellationToken)
  {
    var direct = await db.PermissionAssignments
      .IgnoreQueryFilters()
      .AsNoTracking()
      .Where(x => x.PrincipalKind == principal.Kind && x.PrincipalId == principal.Id && x.IsEnabled)
      .ToListAsync(cancellationToken);

    var rules = PermissionRules.Applicable(direct, principal.TenantId, RuleSource.Direct).ToList();
    if (principal.Kind != PermissionPrincipalKind.User)
    {
      return rules;
    }

    var groups = db.UserGroupMembers
      .IgnoreQueryFilters()
      .Where(x => x.UserId == principal.Id)
      .Select(x => x.UserGroupId);

    var inherited = await db.PermissionAssignments
      .IgnoreQueryFilters()
      .AsNoTracking()
      .Where(x => x.PrincipalKind == PermissionPrincipalKind.UserGroup && groups.Contains(x.PrincipalId) && x.IsEnabled)
      .ToListAsync(cancellationToken);

    rules.AddRange(PermissionRules.Applicable(inherited, principal.TenantId, RuleSource.UserGroup));
    return rules;
  }
}
