using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.AuthorizationLogs;

namespace StealthDesk.Web.Server.Permissions;

/// <summary>
/// Granting, changing and removing permissions, with the rules that keep it safe: who may manage which scope and
/// effect, which scopes make sense for a permission, and that nobody removes from themselves the right to manage
/// access. Every change is logged in the same save.
/// </summary>
public sealed class PermissionAssignmentManager(StealthDeskDb db, IPermissionEvaluator evaluator, IAuthorizationChangeFactory changes)
{
  /// <summary>What the caller may do with permissions, read from their own permission-management permissions.</summary>
  private sealed record Authority(bool ReadsServer, bool ManagesServer, bool ManagesTenant, bool DeniesInTenant);

  public async Task<IResult> ListAsync(Principal actor, Guid tenant, PermissionPrincipalKind kind, Guid principalId, CancellationToken cancellationToken)
  {
    var authority = await AuthorityAsync(actor, tenant, cancellationToken);
    var rows = await Assignments()
      .Where(x => x.PrincipalKind == kind && x.PrincipalId == principalId)
      .Where(x => x.OwningTenantId == tenant || (x.OwningTenantId == null && authority.ReadsServer))
      .OrderBy(x => x.Permission)
      .ThenBy(x => x.ScopeKind)
      .ToListAsync(cancellationToken);
    return Results.Ok(new PermissionAssignmentList { Items = [.. rows.Select(ToContract)] });
  }

  public async Task<IResult> CreateAsync(Principal actor, Guid tenant, CreatePermissionAssignmentRequest request, CancellationToken cancellationToken)
  {
    if (!await PrincipalExistsAsync(request.PrincipalKind, request.PrincipalId, tenant, cancellationToken))
    {
      return Results.Problem($"Principal not found: {request.PrincipalKind}/{request.PrincipalId}.", statusCode: StatusCodes.Status400BadRequest);
    }

    var authority = await AuthorityAsync(actor, tenant, cancellationToken);
    if (WriteRefusal(request.Effect, request.ScopeKind, authority) is { } refusal)
    {
      return Forbidden(refusal);
    }

    if ((NotesRefusal(request.Notes) ?? await ScopeRefusalAsync(request.Permission, request.Effect, request.ScopeKind, request.ScopeId, tenant, cancellationToken)) is { } invalid)
    {
      return Results.Problem(invalid, statusCode: StatusCodes.Status400BadRequest);
    }

    var assignment = NewAssignment(actor, tenant, request.PrincipalKind, request.PrincipalId, request.Permission, request.Effect, request.ScopeKind, request.ScopeId, request.Notes, request.IsEnabled);
    if (await ExistsAsync(assignment, cancellationToken))
    {
      return Duplicate();
    }

    if (IsSelf(actor, request.PrincipalKind, request.PrincipalId)
      && await SelfLockoutAsync(actor, tenant, direct => [.. direct, assignment], cancellationToken) is { } lockout)
    {
      return Results.Problem(lockout, statusCode: StatusCodes.Status400BadRequest);
    }

    db.PermissionAssignments.Add(assignment);
    Log(AuthorizationChangeActions.PermissionAssignmentCreated, actor, assignment, before: null, after: assignment);
    if (await SaveAsync(cancellationToken) is { } conflict)
    {
      return conflict;
    }

    return Results.Created(Routes.PermissionAssignment(assignment.Id), ToContract(assignment));
  }

  public async Task<IResult> UpdateAsync(Principal actor, Guid tenant, Guid id, UpdatePermissionAssignmentRequest request, CancellationToken cancellationToken)
  {
    var authority = await AuthorityAsync(actor, tenant, cancellationToken);
    if (await Assignments().FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } assignment || !Visible(assignment, tenant, authority))
    {
      return Results.NotFound();
    }

    // Both what the assignment was and what it becomes must be the caller's to manage.
    if ((WriteRefusal(assignment.Effect, assignment.ScopeKind, authority) ?? WriteRefusal(request.Effect, request.ScopeKind, authority)) is { } refusal)
    {
      return Forbidden(refusal);
    }

    if ((NotesRefusal(request.Notes) ?? await ScopeRefusalAsync(request.Permission, request.Effect, request.ScopeKind, request.ScopeId, tenant, cancellationToken)) is { } invalid)
    {
      return Results.Problem(invalid, statusCode: StatusCodes.Status400BadRequest);
    }

    var replacement = NewAssignment(actor, tenant, assignment.PrincipalKind, assignment.PrincipalId, request.Permission, request.Effect, request.ScopeKind, request.ScopeId, request.Notes, request.IsEnabled);
    if (await Assignments().AnyAsync(x => x.Id != id && x.PrincipalKind == replacement.PrincipalKind && x.PrincipalId == replacement.PrincipalId
          && x.Permission == replacement.Permission && x.Effect == replacement.Effect && x.ScopeKind == replacement.ScopeKind && x.ScopeId == replacement.ScopeId, cancellationToken))
    {
      return Duplicate();
    }

    if (IsSelf(actor, assignment.PrincipalKind, assignment.PrincipalId)
      && await SelfLockoutAsync(actor, tenant, direct => [.. direct.Where(x => x.Id != id), replacement], cancellationToken) is { } lockout)
    {
      return Results.Problem(lockout, statusCode: StatusCodes.Status400BadRequest);
    }

    var before = PermissionAssignmentSnapshot.Of(assignment);
    assignment.Permission = replacement.Permission;
    assignment.Effect = replacement.Effect;
    assignment.ScopeKind = replacement.ScopeKind;
    assignment.ScopeId = replacement.ScopeId;
    assignment.OwningTenantId = replacement.OwningTenantId;
    assignment.Notes = replacement.Notes;
    assignment.IsEnabled = replacement.IsEnabled;
    db.AuthorizationChanges.Add(changes.Create(
      AuthorizationChangeActions.PermissionAssignmentUpdated,
      actor,
      AuthorizationChangeTargets.PermissionAssignment,
      assignment.Id,
      assignment.OwningTenantId,
      before,
      PermissionAssignmentSnapshot.Of(assignment)));
    if (await SaveAsync(cancellationToken) is { } conflict)
    {
      return conflict;
    }

    return Results.Ok(ToContract(assignment));
  }

  public async Task<IResult> DeleteAsync(Principal actor, Guid tenant, Guid id, CancellationToken cancellationToken)
  {
    var result = await DeleteManyAsync(actor, tenant, [id], cancellationToken);
    return result is IValueHttpResult { Value: DeletePermissionAssignmentsResult deleted }
      ? deleted.SuccessIds.Count == 1 ? Results.NoContent() : Results.NotFound()
      : result;
  }

  /// <summary>Removes the assignments the caller can see; all of them or none when one can't be removed.</summary>
  public async Task<IResult> DeleteManyAsync(Principal actor, Guid tenant, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
  {
    var authority = await AuthorityAsync(actor, tenant, cancellationToken);
    var asked = ids.Distinct().ToList();
    var found = await Assignments().Where(x => asked.Contains(x.Id)).ToListAsync(cancellationToken);
    var visible = found.Where(x => Visible(x, tenant, authority)).ToList();

    foreach (var assignment in visible)
    {
      if (WriteRefusal(assignment.Effect, assignment.ScopeKind, authority) is { } refusal)
      {
        return Forbidden(refusal);
      }
    }

    var removed = visible.Select(x => x.Id).ToHashSet();
    if (visible.Any(x => IsSelf(actor, x.PrincipalKind, x.PrincipalId))
      && await SelfLockoutAsync(actor, tenant, direct => [.. direct.Where(x => !removed.Contains(x.Id))], cancellationToken) is { } lockout)
    {
      return Results.Problem(lockout, statusCode: StatusCodes.Status400BadRequest);
    }

    foreach (var assignment in visible)
    {
      Log(AuthorizationChangeActions.PermissionAssignmentDeleted, actor, assignment, before: assignment, after: null);
    }

    db.PermissionAssignments.RemoveRange(visible);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new DeletePermissionAssignmentsResult([.. removed], [.. asked.Where(x => !removed.Contains(x))]));
  }

  /// <summary>
  /// Grants every permission of the presets at the scope a preset grants it at. Merging adds what the principal
  /// lacks; replacing first removes their assignments at those scopes.
  /// </summary>
  public async Task<IResult> ApplyPresetsAsync(Principal actor, Guid tenant, ApplyPermissionPresetsRequest request, CancellationToken cancellationToken)
  {
    var unknown = request.Presets.Where(x => !PermissionPresets.All.ContainsKey(x)).ToList();
    var permissions = request.Presets.SelectMany(PermissionPresets.PermissionsOf).Distinct().ToList();
    if (unknown.Count > 0 || permissions.Count == 0)
    {
      return Results.Problem(unknown.Count > 0 ? $"Presets not found: {string.Join(", ", unknown)}." : "Choose at least one preset.", statusCode: StatusCodes.Status400BadRequest);
    }

    if (!await PrincipalExistsAsync(request.PrincipalKind, request.PrincipalId, tenant, cancellationToken))
    {
      return Results.Problem($"Principal not found: {request.PrincipalKind}/{request.PrincipalId}.", statusCode: StatusCodes.Status400BadRequest);
    }

    var authority = await AuthorityAsync(actor, tenant, cancellationToken);
    var wanted = permissions
      .Select(x => NewAssignment(actor, tenant, request.PrincipalKind, request.PrincipalId, x, PermissionEffect.Allow, PermissionCatalog.Find(x)!.PresetScope, null, null, true))
      .ToList();
    foreach (var assignment in wanted)
    {
      if (WriteRefusal(assignment.Effect, assignment.ScopeKind, authority) is { } refusal)
      {
        return Forbidden(refusal);
      }
    }

    var existing = await Assignments()
      .Where(x => x.PrincipalKind == request.PrincipalKind && x.PrincipalId == request.PrincipalId)
      .ToListAsync(cancellationToken);
    var scopes = wanted.Select(x => x.ScopeKind).ToHashSet();
    var replaced = request.ReplaceExisting
      ? existing.Where(x => scopes.Contains(x.ScopeKind) && Visible(x, tenant, authority)).ToList()
      : [];
    var kept = existing.Except(replaced).ToList();
    var added = wanted.Where(x => !kept.Any(k => SameGrant(k, x))).ToList();

    if (IsSelf(actor, request.PrincipalKind, request.PrincipalId)
      && await SelfLockoutAsync(actor, tenant, direct => [.. direct.Where(x => !replaced.Any(r => r.Id == x.Id)), .. added], cancellationToken) is { } lockout)
    {
      return Results.Problem(lockout, statusCode: StatusCodes.Status400BadRequest);
    }

    foreach (var assignment in replaced)
    {
      Log(AuthorizationChangeActions.PermissionAssignmentDeleted, actor, assignment, before: assignment, after: null);
    }

    foreach (var assignment in added)
    {
      Log(AuthorizationChangeActions.PermissionAssignmentCreated, actor, assignment, before: null, after: assignment);
    }

    db.PermissionAssignments.RemoveRange(replaced);
    db.PermissionAssignments.AddRange(added);
    if (await SaveAsync(cancellationToken) is { } conflict)
    {
      return conflict;
    }

    return Results.Ok(added.Count);
  }

  /// <summary>Whether a principal of the tenant holds a permission on a resource, and why.</summary>
  public async Task<IResult> EffectiveAsync(
    Guid tenant,
    PermissionPrincipalKind kind,
    Guid principalId,
    string permission,
    PermissionScopeKind scopeKind,
    Guid? scopeId,
    CancellationToken cancellationToken)
  {
    // Another tenant's principal is indistinguishable from one that doesn't exist.
    if (!await PrincipalExistsAsync(kind, principalId, tenant, cancellationToken))
    {
      return Results.NotFound();
    }

    Resource? resource = scopeKind switch
    {
      PermissionScopeKind.Server => Resource.Server,
      PermissionScopeKind.Tenant => Resource.Tenant(tenant),
      PermissionScopeKind.Device when scopeId is { } device && await db.Devices.IgnoreQueryFilters().AnyAsync(x => x.Id == device && x.TenantId == tenant, cancellationToken)
        => Resource.Device(device, tenant),
      PermissionScopeKind.UserGroup when scopeId is { } group && await db.UserGroups.IgnoreQueryFilters().AnyAsync(x => x.Id == group && x.TenantId == tenant, cancellationToken)
        => Resource.UserGroup(group, tenant),
      _ => null,
    };
    if (resource is null)
    {
      return Results.Problem("Choose a server, tenant, device or user group of this tenant to check.", statusCode: StatusCodes.Status400BadRequest);
    }

    var decision = await evaluator.EvaluateAsync(new Principal(kind, principalId, tenant), permission, resource, cancellationToken);
    return Results.Ok(new EffectivePermission(decision.Allowed, decision.Reason));
  }

  private IQueryable<PermissionAssignmentRecord> Assignments() => db.PermissionAssignments.IgnoreQueryFilters();

  private async Task<Authority> AuthorityAsync(Principal actor, Guid tenant, CancellationToken cancellationToken)
  {
    var rules = await evaluator.RulesAsync(actor, cancellationToken);
    bool Holds(string permission, Resource resource) => PermissionRules.Evaluate(rules, permission, resource).Allowed;
    return new Authority(
      Holds(PermissionNames.ServerPermissionsRead, Resource.Server),
      Holds(PermissionNames.ServerPermissionsWrite, Resource.Server),
      Holds(PermissionNames.TenantPermissionsWrite, Resource.Tenant(tenant)),
      Holds(PermissionNames.TenantPermissionsDeny, Resource.Tenant(tenant)));
  }

  // Delegated administration: managing the tenant's permissions lets the holder grant permissions they don't hold
  // themselves. Server scope needs the server's permission manager, and a deny also needs the right to deny.
  private static string? WriteRefusal(PermissionEffect effect, PermissionScopeKind scope, Authority authority)
  {
    var (managed, needed) = scope == PermissionScopeKind.Server
      ? (authority.ManagesServer, PermissionNames.ServerPermissionsWrite)
      : (authority.ManagesTenant, PermissionNames.TenantPermissionsWrite);
    if (!managed)
    {
      return $"The '{needed}' permission is required to manage {scope.ToString().ToLowerInvariant()}-scoped assignments.";
    }

    return effect == PermissionEffect.Deny && !authority.DeniesInTenant
      ? $"The '{PermissionNames.TenantPermissionsDeny}' permission is required to manage deny assignments."
      : null;
  }

  // A tenant's rows are its own; server rows (no tenant) need the server's permissions to be seen or changed.
  private static bool Visible(PermissionAssignmentRecord assignment, Guid tenant, Authority authority) =>
    assignment.OwningTenantId == tenant || (assignment.OwningTenantId is null && (authority.ReadsServer || authority.ManagesServer));

  private async Task<string?> ScopeRefusalAsync(string permission, PermissionEffect effect, PermissionScopeKind scope, Guid? scopeId, Guid tenant, CancellationToken cancellationToken)
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
    // belong to no tenant may hold one, and users and groups always belong to one.
    if (effect == PermissionEffect.Allow && scope == PermissionScopeKind.Server && info.AllowsTenantScope)
    {
      return "Server-scoped allows of tenant permissions can't be given to users or user groups.";
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

  private static string? NotesRefusal(string? notes) =>
    notes?.Length > CreatePermissionAssignmentRequest.NotesMax ? $"Notes can't be longer than {CreatePermissionAssignmentRequest.NotesMax} characters." : null;

  private Task<bool> PrincipalExistsAsync(PermissionPrincipalKind kind, Guid id, Guid tenant, CancellationToken cancellationToken) => kind switch
  {
    PermissionPrincipalKind.User => db.Users.IgnoreQueryFilters().AnyAsync(x => x.Id == id && x.TenantId == tenant, cancellationToken),
    PermissionPrincipalKind.UserGroup => db.UserGroups.IgnoreQueryFilters().AnyAsync(x => x.Id == id && x.TenantId == tenant, cancellationToken),
    _ => Task.FromResult(false),
  };

  /// <summary>
  /// Replays the caller's own assignments with a change applied and refuses it when they would lose a permission
  /// they need to keep managing access (one the catalog marks as not self-removable).
  /// </summary>
  private async Task<string?> SelfLockoutAsync(
    Principal actor,
    Guid tenant,
    Func<List<PermissionAssignmentRecord>, List<PermissionAssignmentRecord>> change,
    CancellationToken cancellationToken)
  {
    var direct = await Assignments().AsNoTracking()
      .Where(x => x.PrincipalKind == PermissionPrincipalKind.User && x.PrincipalId == actor.Id)
      .ToListAsync(cancellationToken);
    var groups = db.UserGroupMembers.IgnoreQueryFilters().Where(x => x.UserId == actor.Id).Select(x => x.UserGroupId);
    var inherited = PermissionRules.Applicable(
      await Assignments().AsNoTracking().Where(x => x.PrincipalKind == PermissionPrincipalKind.UserGroup && groups.Contains(x.PrincipalId)).ToListAsync(cancellationToken),
      tenant,
      RuleSource.UserGroup).ToList();

    List<PermissionRule> Rules(IEnumerable<PermissionAssignmentRecord> rows) => [.. PermissionRules.Applicable(rows, tenant, RuleSource.Direct), .. inherited];
    var before = Rules(direct);
    var after = Rules(change(direct));

    foreach (var info in PermissionCatalog.All.Where(x => !x.SelfRemovable))
    {
      var resource = info.AllowsTenantScope ? Resource.Tenant(tenant) : Resource.Server;
      if (PermissionRules.Evaluate(before, info.Name, resource).Allowed && !PermissionRules.Evaluate(after, info.Name, resource).Allowed)
      {
        return $"You can't remove '{info.DisplayName}' from yourself: you need it to keep managing permissions.";
      }
    }

    return null;
  }

  private static bool IsSelf(Principal actor, PermissionPrincipalKind kind, Guid id) =>
    kind == PermissionPrincipalKind.User && actor.Kind == PermissionPrincipalKind.User && id == actor.Id;

  private Task<bool> ExistsAsync(PermissionAssignmentRecord candidate, CancellationToken cancellationToken) =>
    Assignments().AnyAsync(x => x.PrincipalKind == candidate.PrincipalKind && x.PrincipalId == candidate.PrincipalId
      && x.Permission == candidate.Permission && x.Effect == candidate.Effect && x.ScopeKind == candidate.ScopeKind && x.ScopeId == candidate.ScopeId, cancellationToken);

  private static bool SameGrant(PermissionAssignmentRecord a, PermissionAssignmentRecord b) =>
    a.Permission == b.Permission && a.Effect == b.Effect && a.ScopeKind == b.ScopeKind && a.ScopeId == b.ScopeId;

  // Server scope names nothing and belongs to no tenant; tenant scope names the tenant acted in.
  private static PermissionAssignmentRecord NewAssignment(
    Principal actor,
    Guid tenant,
    PermissionPrincipalKind kind,
    Guid principalId,
    string permission,
    PermissionEffect effect,
    PermissionScopeKind scope,
    Guid? scopeId,
    string? notes,
    bool enabled) => new()
  {
    Id = Guid.NewGuid(),
    PrincipalKind = kind,
    PrincipalId = principalId,
    Permission = permission,
    Effect = effect,
    ScopeKind = scope,
    ScopeId = scope switch
    {
      PermissionScopeKind.Server => null,
      PermissionScopeKind.Tenant => tenant,
      _ => scopeId,
    },
    OwningTenantId = scope == PermissionScopeKind.Server ? null : tenant,
    Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
    IsEnabled = enabled,
    CreatedByKind = actor.Kind.ToString(),
    CreatedById = actor.Id,
  };

  private void Log(string action, Principal actor, PermissionAssignmentRecord target, PermissionAssignmentRecord? before, PermissionAssignmentRecord? after) =>
    db.AuthorizationChanges.Add(changes.Create(
      action,
      actor,
      AuthorizationChangeTargets.PermissionAssignment,
      target.Id,
      target.OwningTenantId,
      before is null ? null : PermissionAssignmentSnapshot.Of(before),
      after is null ? null : PermissionAssignmentSnapshot.Of(after)));

  // Two identical grants saved at the same moment: the unique index refuses the second.
  private async Task<IResult?> SaveAsync(CancellationToken cancellationToken)
  {
    try
    {
      await db.SaveChangesAsync(cancellationToken);
      return null;
    }
    catch (DbUpdateException)
    {
      return Duplicate();
    }
  }

  private static IResult Duplicate() => Results.Problem("An identical permission assignment already exists.", statusCode: StatusCodes.Status409Conflict);

  private static IResult Forbidden(string reason) => Results.Problem(reason, statusCode: StatusCodes.Status403Forbidden);

  private static PermissionAssignment ToContract(PermissionAssignmentRecord x) =>
    new(x.Id, x.PrincipalKind, x.PrincipalId, x.Permission, x.Effect, x.ScopeKind, x.ScopeId, x.Notes, x.IsEnabled, x.CreatedAt);
}
