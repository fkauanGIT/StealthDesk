using System.Security.Claims;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.AuthorizationLogs;

public static class AuthorizationLogEndpoints
{
  public static IEndpointRouteBuilder MapAuthorizationLogEndpoints(this IEndpointRouteBuilder endpoints)
  {
    // Two audiences read a tenant's entries: whoever reads every tenant's log (server permission) picks the tenant,
    // and whoever reads their own tenant's log gets only that one. No single policy says "either", so it is
    // checked here.
    endpoints.MapGet(Routes.AuthorizationLogs, async (
        Guid tenantId,
        [AsParameters] QueryParameters query,
        ClaimsPrincipal user,
        IPermissionEvaluator evaluator,
        StealthDeskDb db,
        CancellationToken cancellationToken) =>
      {
        if (Principal.From(user) is not { } principal)
        {
          return Results.Forbid();
        }

        var rules = await evaluator.RulesAsync(principal, cancellationToken);
        var readsEveryTenant = PermissionRules.Evaluate(rules, PermissionNames.ServerAuthorizationLogsRead, Resource.Server).Allowed;
        var readsOwnTenant = principal.TenantId == tenantId
          && PermissionRules.Evaluate(rules, PermissionNames.TenantAuthorizationLogsRead, Resource.Tenant(tenantId)).Allowed;
        if (!readsEveryTenant && !readsOwnTenant)
        {
          return Results.Forbid();
        }

        return await PageAsync(db, db.AuthorizationChanges.Where(x => x.OwningTenantId == tenantId), query.ToQuery(), cancellationToken);
      })
      .RequireAuthorization()
      .ChecksPermission(PermissionNames.ServerAuthorizationLogsRead)
      .ChecksPermission(PermissionNames.TenantAuthorizationLogsRead);

    // Server-wide changes belong to no tenant, so the tenant route can't reach them.
    endpoints.MapGet(Routes.ServerAuthorizationLogs, (
        [AsParameters] QueryParameters query,
        StealthDeskDb db,
        CancellationToken cancellationToken) =>
      PageAsync(db, db.AuthorizationChanges.Where(x => x.OwningTenantId == null), query.ToQuery(), cancellationToken))
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.ServerAuthorizationLogsRead));

    return endpoints;
  }

  // Every filter and the page are optional in the query string; bound as the contract's query, page and size
  // would be required.
  private readonly record struct QueryParameters(
    string? Action,
    string? ActorKind,
    string? TargetKind,
    string? Search,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int? Page,
    int? PageSize)
  {
    public AuthorizationChangeQuery ToQuery() => new()
    {
      Action = Action,
      ActorKind = ActorKind,
      TargetKind = TargetKind,
      Search = Search,
      From = From,
      To = To,
      Page = Page ?? 0,
      PageSize = PageSize ?? new AuthorizationChangeQuery().PageSize,
    };
  }

  private static async Task<IResult> PageAsync(
    StealthDeskDb db,
    IQueryable<AuthorizationChangeRecord> entries,
    AuthorizationChangeQuery query,
    CancellationToken cancellationToken)
  {
    if (query.Search?.Length > AuthorizationChangeQuery.SearchMax)
    {
      return Results.ValidationProblem(new Dictionary<string, string[]>
      {
        [nameof(query.Search)] = [$"Search for at most {AuthorizationChangeQuery.SearchMax} characters."],
      });
    }

    entries = Filter(db, entries.AsNoTracking(), query);
    var total = await entries.CountAsync(cancellationToken);

    var pageSize = Math.Clamp(query.PageSize, 1, AuthorizationChangeQuery.MaxPageSize);
    // Kept small enough that page * size can't overflow into a negative offset.
    var page = Math.Clamp(query.Page, 0, int.MaxValue / pageSize);
    var items = await entries
      .OrderByDescending(x => x.CreatedAt)
      .ThenByDescending(x => x.Id)
      .Skip(page * pageSize)
      .Take(pageSize)
      .Select(x => new AuthorizationChangeEntry(
        x.Id,
        x.Action,
        x.ActorKind,
        x.ActorId,
        x.TargetKind,
        x.TargetId,
        x.OwningTenantId,
        x.IpAddress,
        x.CreatedAt,
        x.BeforeJson,
        x.AfterJson))
      .ToListAsync(cancellationToken);

    return Results.Ok(new AuthorizationChangePage { Items = items, TotalItems = total });
  }

  private static IQueryable<AuthorizationChangeRecord> Filter(StealthDeskDb db, IQueryable<AuthorizationChangeRecord> entries, AuthorizationChangeQuery query)
  {
    if (!string.IsNullOrWhiteSpace(query.Action))
    {
      entries = entries.Where(x => x.Action == query.Action);
    }

    if (!string.IsNullOrWhiteSpace(query.ActorKind))
    {
      entries = entries.Where(x => x.ActorKind == query.ActorKind);
    }

    if (!string.IsNullOrWhiteSpace(query.TargetKind))
    {
      entries = entries.Where(x => x.TargetKind == query.TargetKind);
    }

    if (query.From is { } from)
    {
      entries = entries.Where(x => x.CreatedAt >= from);
    }

    if (query.To is { } to)
    {
      entries = entries.Where(x => x.CreatedAt <= to);
    }

    if (query.Search?.Trim() is not { Length: > 0 } search)
    {
      return entries;
    }

    if (Guid.TryParse(search, out var id))
    {
      return entries.Where(x => x.ActorId == id || x.TargetId == id);
    }

    if (db.Database.IsInMemory())
    {
      return entries.Where(x =>
        (x.ActorId != null && x.ActorId.Value.ToString().Contains(search, StringComparison.OrdinalIgnoreCase))
        || (x.TargetId != null && x.TargetId.Value.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)));
    }

    // Part of an id: the user's text is matched literally, so '%' and '_' are not wildcards.
    var pattern = $"%{EscapeLike(search)}%";
    return entries.Where(x =>
      (x.ActorId != null && EF.Functions.ILike(x.ActorId.Value.ToString(), pattern, LikeEscape))
      || (x.TargetId != null && EF.Functions.ILike(x.TargetId.Value.ToString(), pattern, LikeEscape)));
  }

  private const string LikeEscape = "\\";

  private static string EscapeLike(string text) =>
    text.Replace(LikeEscape, LikeEscape + LikeEscape).Replace("%", LikeEscape + "%").Replace("_", LikeEscape + "_");
}
