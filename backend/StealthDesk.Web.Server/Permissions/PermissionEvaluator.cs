using StealthDesk.Contracts.PersonalAccessTokens;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.ServiceAccounts;

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

  /// <summary>The rules that apply to the principal, for checks that become a database filter instead of one decision.</summary>
  Task<PermissionRuleSet> RulesAsync(Principal principal, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the assignments on every call instead of caching them, so a change (a removed preset, a new deny) applies
/// to the very next request.
/// </summary>
public sealed class PermissionEvaluator(StealthDeskDb db) : IPermissionEvaluator
{
  public async Task<PermissionDecision> EvaluateAsync(Principal principal, string permission, Resource resource, CancellationToken cancellationToken = default) =>
    PermissionRules.Evaluate(await RulesAsync(principal, cancellationToken), permission, await WithGroupsAsync(resource, cancellationToken));

  public async Task<IReadOnlyDictionary<string, PermissionDecision>> EvaluateManyAsync(
    Principal principal,
    IReadOnlyCollection<string> permissions,
    Resource resource,
    CancellationToken cancellationToken = default)
  {
    var rules = await RulesAsync(principal, cancellationToken);
    resource = await WithGroupsAsync(resource, cancellationToken);
    return permissions
      .Distinct(StringComparer.Ordinal)
      .ToDictionary(x => x, x => PermissionRules.Evaluate(rules, x, resource), StringComparer.Ordinal);
  }

  // A device is decided with the groups it is in, read here so no caller can leave them out.
  private async Task<Resource> WithGroupsAsync(Resource resource, CancellationToken cancellationToken)
  {
    if (resource is not { Kind: PermissionScopeKind.Device, Id: { } device, DeviceGroups: null })
    {
      return resource;
    }

    var groups = await db.DeviceGroupMembers.AsNoTracking().Where(x => x.DeviceId == device).Select(x => x.DeviceGroupId).ToListAsync(cancellationToken);
    return resource with { DeviceGroups = groups };
  }

  // A personal access token acts as its owner: inheriting, the owner's rules decide; restricted, the token's own
  // grants decide and the owner must hold the same. A token that no longer exists grants nothing.
  public async Task<PermissionRuleSet> RulesAsync(Principal principal, CancellationToken cancellationToken = default)
  {
    // Only a server account, and only one created unrestricted, skips the rules.
    if (principal is { Kind: PermissionPrincipalKind.ServiceAccount, TenantId: null }
      && await db.ServiceAccounts.AnyAsync(x => x.Id == principal.Id && x.Kind == ServiceAccountKind.Server && x.AccessMode == ServiceAccountAccessMode.Unrestricted, cancellationToken))
    {
      return new PermissionRuleSet([], Unrestricted: true);
    }

    var owner = await OwnRulesAsync(principal, cancellationToken);
    if (principal.TokenId is not { } tokenId)
    {
      return new PermissionRuleSet(owner);
    }

    var mode = await db.PersonalAccessTokens
      .IgnoreQueryFilters()
      .Where(x => x.Id == tokenId && x.UserId == principal.Id)
      .Select(x => (PersonalAccessTokenMode?)x.Mode)
      .FirstOrDefaultAsync(cancellationToken);
    if (mode == PersonalAccessTokenMode.InheritOwner)
    {
      return new PermissionRuleSet(owner);
    }

    var granted = mode is null
      ? []
      : await db.PermissionAssignments
        .IgnoreQueryFilters()
        .AsNoTracking()
        .Where(x => x.PrincipalKind == PermissionPrincipalKind.PersonalAccessToken && x.PrincipalId == tokenId && x.IsEnabled)
        .ToListAsync(cancellationToken);
    return new PermissionRuleSet([.. PermissionRules.Applicable(granted, principal.TenantId, RuleSource.Direct)], owner);
  }

  // The principal's own assignments and, for a user, those of every group they are in. The tenant query filters
  // are bypassed: the rules apply the tenant boundary themselves, for any principal.
  private async Task<IReadOnlyCollection<PermissionRule>> OwnRulesAsync(Principal principal, CancellationToken cancellationToken)
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
