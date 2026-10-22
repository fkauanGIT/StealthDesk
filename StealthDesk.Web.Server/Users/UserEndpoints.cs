using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.Users;
using StealthDesk.Web.Server.Accounts;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.Users;

/// <summary>
/// A tenant's users. People normally join through invites; creating a user and resetting a password are here for
/// scripts and integrations. Every route acts in the tenant named by <c>?tenantId=</c>, which must be the caller's
/// own (the default) unless the caller is server-wide.
/// </summary>
public static class UserEndpoints
{
  public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder endpoints)
  {
    endpoints.MapGet(Routes.Users, async (Guid? tenantId, ClaimsPrincipal user, StealthDeskDb db, CancellationToken cancellationToken) =>
      {
        if (Tenant(user, tenantId) is not { } tenant)
        {
          return Results.Forbid();
        }

        var users = await InTenant(db, tenant)
          .OrderBy(x => x.UserName)
          .ThenBy(x => x.Id)
          .Select(x => new { x.Id, x.UserName, x.Email, x.CreatedAt })
          .ToListAsync(cancellationToken);

        var ids = users.Select(x => x.Id).ToList();
        var granted = await db.PermissionAssignments
          .IgnoreQueryFilters()
          .Where(x => x.PrincipalKind == PermissionPrincipalKind.User && ids.Contains(x.PrincipalId) && x.Effect == PermissionEffect.Allow && x.IsEnabled)
          .Select(x => new { x.PrincipalId, x.Permission })
          .ToListAsync(cancellationToken);
        var byUser = granted.ToLookup(x => x.PrincipalId, x => x.Permission);

        return Results.Ok(new TenantUserList
        {
          Items = [.. users.Select(x => new TenantUser(x.Id, x.UserName ?? string.Empty, x.Email, x.CreatedAt, [.. byUser[x.Id].Distinct().Order()]))],
        });
      })
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantUsersRead));

    endpoints.MapPost(Routes.Users, CreateAsync)
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantUsersWrite));

    endpoints.MapPost($"{Routes.Users}/{{id:guid}}/reset-password", async (
        Guid id,
        Guid? tenantId,
        ClaimsPrincipal caller,
        UserManager<UserRecord> users,
        StealthDeskDb db,
        IOptions<IdentityOptions> identity) =>
      {
        if (Tenant(caller, tenantId) is not { } tenant)
        {
          return Results.Forbid();
        }

        if (await InTenant(db, tenant).FirstOrDefaultAsync(x => x.Id == id) is not { } user)
        {
          return Results.NotFound();
        }

        var password = TemporaryPasswords.Generate(identity.Value.Password);
        var reset = await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password);
        if (!reset.Succeeded)
        {
          return ManageEndpoints.ValidationProblem(reset);
        }

        user.MustChangePassword = true;
        await users.UpdateAsync(user);
        return Results.Ok(new TemporaryPassword(password));
      })
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantUsersWrite));

    endpoints.MapDelete($"{Routes.Users}/{{id:guid}}", async (
        Guid id,
        Guid? tenantId,
        ClaimsPrincipal caller,
        UserManager<UserRecord> users,
        StealthDeskDb db) =>
      {
        if (Tenant(caller, tenantId) is not { } tenant)
        {
          return Results.Forbid();
        }

        if (Principal.From(caller)?.Id == id)
        {
          return Results.Problem("You can't delete your own account here; use your account settings.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (await InTenant(db, tenant).FirstOrDefaultAsync(x => x.Id == id) is not { } user)
        {
          return Results.NotFound();
        }

        var deleted = await users.DeleteAsync(user);
        return deleted.Succeeded ? Results.NoContent() : ManageEndpoints.ValidationProblem(deleted);
      })
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantUsersDelete));

    return endpoints;
  }

  private static async Task<IResult> CreateAsync(
    Guid? tenantId,
    CreateUserRequest request,
    ClaimsPrincipal caller,
    UserManager<UserRecord> users,
    StealthDeskDb db,
    IPermissionEvaluator evaluator,
    PermissionSeeder seeder,
    CancellationToken cancellationToken)
  {
    if (Principal.From(caller) is not { } principal || principal.TenantFor(tenantId ?? Guid.Empty) is not { } tenant)
    {
      return Results.Forbid();
    }

    // A server-wide caller names the tenant; it has to exist, or the user would belong to none.
    if (!await db.Tenants.IgnoreQueryFilters().AnyAsync(x => x.Id == tenant, cancellationToken))
    {
      return Results.Problem("Tenant not found.", statusCode: StatusCodes.Status400BadRequest);
    }

    var presets = request.Presets?.Distinct().ToList() ?? [];
    var unknown = presets.Where(x => !PermissionPresets.All.ContainsKey(x)).ToList();
    if (unknown.Count > 0)
    {
      return Results.Problem($"Presets not found: {string.Join(", ", unknown)}.", statusCode: StatusCodes.Status400BadRequest);
    }

    if (!await MayGrantAsync(evaluator, principal, tenant, presets, cancellationToken))
    {
      return Results.Forbid();
    }

    var email = string.IsNullOrWhiteSpace(request.Email) ? request.UserName : request.Email;
    var user = new UserRecord { UserName = email, Email = email, TenantId = tenant };
    var created = await TenantMembers.CreateAsync(users, seeder, user, request.Password, presets, cancellationToken);
    if (!created.Succeeded)
    {
      return ManageEndpoints.ValidationProblem(created);
    }

    var permissions = await db.PermissionAssignments
      .IgnoreQueryFilters()
      .Where(x => x.PrincipalKind == PermissionPrincipalKind.User && x.PrincipalId == user.Id && x.Effect == PermissionEffect.Allow && x.IsEnabled)
      .Select(x => x.Permission)
      .Distinct()
      .OrderBy(x => x)
      .ToListAsync(cancellationToken);

    return Results.Created(
      $"{Routes.Users}?tenantId={tenant}",
      new TenantUser(user.Id, user.UserName!, user.Email, user.CreatedAt, permissions));
  }

  // A preset grants what it contains, so giving one needs the right to grant that: managing the tenant's
  // permissions, the tenant administrator preset also denying them, and the server administrator preset managing
  // the server's.
  private static async Task<bool> MayGrantAsync(
    IPermissionEvaluator evaluator,
    Principal principal,
    Guid tenant,
    IReadOnlyCollection<string> presets,
    CancellationToken cancellationToken)
  {
    var server = presets.Contains(PermissionPresets.ServerAdministrator);
    var tenantAdministrator = presets.Contains(PermissionPresets.TenantAdministrator);
    var inTenant = presets.SelectMany(PermissionPresets.PermissionsOf)
      .Any(x => PermissionCatalog.Find(x)?.PresetScope == PermissionScopeKind.Tenant);
    if (!server && !inTenant)
    {
      return true;
    }

    var rules = await evaluator.RulesAsync(principal, cancellationToken);
    var managesServer = PermissionRules.Evaluate(rules, PermissionNames.ServerPermissionsWrite, Resource.Server).Allowed;
    var managesTenant = PermissionRules.Evaluate(rules, PermissionNames.TenantPermissionsWrite, Resource.Tenant(tenant)).Allowed;
    var deniesInTenant = PermissionRules.Evaluate(rules, PermissionNames.TenantPermissionsDeny, Resource.Tenant(tenant)).Allowed;

    return (!server || managesServer)
      && (!inTenant || managesServer || managesTenant)
      && (!tenantAdministrator || managesServer || (managesTenant && deniesInTenant));
  }

  private static Guid? Tenant(ClaimsPrincipal caller, Guid? requested) => Principal.From(caller)?.TenantFor(requested ?? Guid.Empty);

  private static IQueryable<UserRecord> InTenant(StealthDeskDb db, Guid tenant) =>
    db.Users.IgnoreQueryFilters().Where(x => x.TenantId == tenant);
}
