using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.UserGroups;
using StealthDesk.Web.Server.AuthorizationLogs;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.UserGroups;

/// <summary>
/// A tenant's user groups. Managing the groups needs the tenant's group permissions; adding and removing members
/// needs <c>user-group.assign-users</c>, which can be granted for a single group. Every change is logged with it.
/// A group of another tenant answers 404, like one that doesn't exist.
/// </summary>
public static class UserGroupEndpoints
{
  public static IEndpointRouteBuilder MapUserGroupEndpoints(this IEndpointRouteBuilder endpoints)
  {
    endpoints.MapGet(Routes.UserGroups, async (Guid? tenantId, ClaimsPrincipal caller, StealthDeskDb db, CancellationToken cancellationToken) =>
      {
        if (Tenant(caller, tenantId) is not { } tenant)
        {
          return Results.Forbid();
        }

        var groups = await InTenant(db, tenant)
          .OrderBy(x => x.Name)
          .Select(x => new UserGroupSummary(x.Id, x.Name, x.Description, x.CreatedAt, x.Members.Count))
          .ToListAsync(cancellationToken);
        return Results.Ok(new UserGroupList { Items = groups });
      })
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantUserGroupsRead));

    endpoints.MapGet($"{Routes.UserGroups}/{{id:guid}}", async (Guid id, Guid? tenantId, ClaimsPrincipal caller, StealthDeskDb db, CancellationToken cancellationToken) =>
        Tenant(caller, tenantId) is not { } tenant ? Results.Forbid()
        : await DetailAsync(db, id, tenant, cancellationToken) is { } detail ? Results.Ok(detail)
        : Results.NotFound())
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantUserGroupsRead));

    endpoints.MapPost(Routes.UserGroups, async (
        Guid? tenantId,
        UserGroupRequest request,
        ClaimsPrincipal caller,
        StealthDeskDb db,
        IAuthorizationChangeFactory changes,
        CancellationToken cancellationToken) =>
      {
        if (Tenant(caller, tenantId) is not { } tenant)
        {
          return Results.Forbid();
        }

        if (Invalid(request) is { } invalid)
        {
          return invalid;
        }

        var name = request.Name.Trim();
        if (await InTenant(db, tenant).AnyAsync(x => x.Name == name, cancellationToken))
        {
          return NameTaken();
        }

        var group = new UserGroupRecord { Id = Guid.NewGuid(), TenantId = tenant, Name = name, Description = Clean(request.Description) };
        db.UserGroups.Add(group);
        db.AuthorizationChanges.Add(changes.Create(
          AuthorizationChangeActions.UserGroupCreated,
          Principal.From(caller),
          AuthorizationChangeTargets.UserGroup,
          group.Id,
          tenant,
          after: new UserGroupSnapshot(group.Name, group.Description)));
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created($"{Routes.UserGroup(group.Id)}?tenantId={tenant}", await DetailAsync(db, group.Id, tenant, cancellationToken));
      })
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantUserGroupsWrite));

    endpoints.MapPut($"{Routes.UserGroups}/{{id:guid}}", async (
        Guid id,
        Guid? tenantId,
        UserGroupRequest request,
        ClaimsPrincipal caller,
        StealthDeskDb db,
        IAuthorizationChangeFactory changes,
        CancellationToken cancellationToken) =>
      {
        if (Tenant(caller, tenantId) is not { } tenant)
        {
          return Results.Forbid();
        }

        if (Invalid(request) is { } invalid)
        {
          return invalid;
        }

        if (await InTenant(db, tenant).FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } group)
        {
          return Results.NotFound();
        }

        var name = request.Name.Trim();
        if (await InTenant(db, tenant).AnyAsync(x => x.Name == name && x.Id != id, cancellationToken))
        {
          return NameTaken();
        }

        var before = new UserGroupSnapshot(group.Name, group.Description);
        group.Name = name;
        group.Description = Clean(request.Description);
        db.AuthorizationChanges.Add(changes.Create(
          AuthorizationChangeActions.UserGroupUpdated,
          Principal.From(caller),
          AuthorizationChangeTargets.UserGroup,
          id,
          tenant,
          before,
          new UserGroupSnapshot(group.Name, group.Description)));
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(await DetailAsync(db, id, tenant, cancellationToken));
      })
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantUserGroupsWrite));

    // The group's own assignments go with it; its members simply stop receiving them.
    endpoints.MapDelete($"{Routes.UserGroups}/{{id:guid}}", async (
        Guid id,
        Guid? tenantId,
        ClaimsPrincipal caller,
        StealthDeskDb db,
        IAuthorizationChangeFactory changes,
        CancellationToken cancellationToken) =>
      {
        if (Tenant(caller, tenantId) is not { } tenant)
        {
          return Results.Forbid();
        }

        if (await InTenant(db, tenant).Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } group)
        {
          return Results.NotFound();
        }

        db.PermissionAssignments.RemoveRange(await db.PermissionAssignments
          .IgnoreQueryFilters()
          .Where(x => x.PrincipalKind == PermissionPrincipalKind.UserGroup && x.PrincipalId == id)
          .ToListAsync(cancellationToken));
        db.UserGroupMembers.RemoveRange(group.Members);
        db.UserGroups.Remove(group);
        db.AuthorizationChanges.Add(changes.Create(
          AuthorizationChangeActions.UserGroupDeleted,
          Principal.From(caller),
          AuthorizationChangeTargets.UserGroup,
          id,
          tenant,
          before: new UserGroupSnapshot(group.Name, group.Description)));
        await db.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
      })
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantUserGroupsWrite));

    endpoints.MapPost($"{Routes.UserGroups}/{{id:guid}}/members", (
        Guid id,
        Guid? tenantId,
        UserGroupMembersRequest request,
        ClaimsPrincipal caller,
        IAuthorizationService authorization,
        StealthDeskDb db,
        IAuthorizationChangeFactory changes,
        CancellationToken cancellationToken) =>
      ChangeMembersAsync(id, tenantId, request, caller, authorization, db, changes, add: true, cancellationToken))
      .RequireAuthorization()
      .ChecksPermission(PermissionNames.UserGroupAssignUsers);

    endpoints.MapDelete($"{Routes.UserGroups}/{{id:guid}}/members", (
        Guid id,
        Guid? tenantId,
        [Microsoft.AspNetCore.Mvc.FromBody] UserGroupMembersRequest request,
        ClaimsPrincipal caller,
        IAuthorizationService authorization,
        StealthDeskDb db,
        IAuthorizationChangeFactory changes,
        CancellationToken cancellationToken) =>
      ChangeMembersAsync(id, tenantId, request, caller, authorization, db, changes, add: false, cancellationToken))
      .RequireAuthorization()
      .ChecksPermission(PermissionNames.UserGroupAssignUsers);

    return endpoints;
  }

  // The permission is checked on the group itself, so a grant for one group lets its holder manage only that one.
  private static async Task<IResult> ChangeMembersAsync(
    Guid id,
    Guid? tenantId,
    UserGroupMembersRequest request,
    ClaimsPrincipal caller,
    IAuthorizationService authorization,
    StealthDeskDb db,
    IAuthorizationChangeFactory changes,
    bool add,
    CancellationToken cancellationToken)
  {
    if (Tenant(caller, tenantId) is not { } tenant)
    {
      return Results.Forbid();
    }

    if (!(await authorization.AuthorizeAsync(caller, Resource.UserGroup(id, tenant), PermissionPolicies.For(PermissionNames.UserGroupAssignUsers))).Succeeded)
    {
      return Results.Forbid();
    }

    if (await InTenant(db, tenant).Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } group)
    {
      return Results.NotFound();
    }

    var current = group.Members.Select(x => x.UserId).ToHashSet();
    var asked = request.UserIds.Distinct().ToList();
    int changed;
    if (add)
    {
      var joining = asked.Where(x => !current.Contains(x)).ToList();
      if (joining.Count > 0
        && await db.Users.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant && joining.Contains(x.Id), cancellationToken) != joining.Count)
      {
        return Results.Problem("One or more users were not found in this tenant.", statusCode: StatusCodes.Status400BadRequest);
      }

      db.UserGroupMembers.AddRange(joining.Select(x => new UserGroupMemberRecord { UserGroupId = id, UserId = x }));
      changed = joining.Count;
    }
    else
    {
      var leaving = group.Members.Where(x => asked.Contains(x.UserId)).ToList();
      db.UserGroupMembers.RemoveRange(leaving);
      changed = leaving.Count;
    }

    if (changed > 0)
    {
      db.AuthorizationChanges.Add(changes.Create(
        add ? AuthorizationChangeActions.UserGroupMembersAdded : AuthorizationChangeActions.UserGroupMembersRemoved,
        Principal.From(caller),
        AuthorizationChangeTargets.UserGroup,
        id,
        tenant,
        after: new UserGroupMembershipChange(changed)));
      await db.SaveChangesAsync(cancellationToken);
    }

    return Results.NoContent();
  }

  private static async Task<UserGroupDetail?> DetailAsync(StealthDeskDb db, Guid id, Guid tenant, CancellationToken cancellationToken)
  {
    var group = await InTenant(db, tenant).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (group is null)
    {
      return null;
    }

    var members = await db.UserGroupMembers
      .IgnoreQueryFilters()
      .Where(x => x.UserGroupId == id)
      .Select(x => new UserGroupMember(x.UserId, x.User!.UserName ?? string.Empty, x.User.LastSignIn))
      .ToListAsync(cancellationToken);
    return new UserGroupDetail(group.Id, group.Name, group.Description, group.CreatedAt, [.. members.OrderBy(x => x.UserName, StringComparer.OrdinalIgnoreCase)]);
  }

  private static IResult? Invalid(UserGroupRequest request)
  {
    var errors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > UserGroupRequest.NameMax)
    {
      errors[nameof(request.Name)] = [$"Enter a name of at most {UserGroupRequest.NameMax} characters."];
    }

    if (request.Description?.Length > UserGroupRequest.DescriptionMax)
    {
      errors[nameof(request.Description)] = [$"Use at most {UserGroupRequest.DescriptionMax} characters."];
    }

    return errors.Count == 0 ? null : Results.ValidationProblem(errors);
  }

  private static IResult NameTaken() => Results.Problem("A group with that name already exists.", statusCode: StatusCodes.Status409Conflict);

  private static string? Clean(string? description) => string.IsNullOrWhiteSpace(description) ? null : description.Trim();

  private static Guid? Tenant(ClaimsPrincipal caller, Guid? requested) => Principal.From(caller)?.TenantFor(requested ?? Guid.Empty);

  private static IQueryable<UserGroupRecord> InTenant(StealthDeskDb db, Guid tenant) =>
    db.UserGroups.IgnoreQueryFilters().Where(x => x.TenantId == tenant);
}
