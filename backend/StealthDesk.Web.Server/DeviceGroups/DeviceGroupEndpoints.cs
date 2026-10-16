using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.DeviceGroups;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.AuthorizationLogs;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.DeviceGroups;

/// <summary>
/// A tenant's device groups. Managing the groups needs the tenant's device group permissions; adding, removing and
/// moving devices needs <c>device-group.assign-devices</c> on each group touched, which can be granted for a single
/// group. Every change is logged with it. A group of another tenant answers 404, like one that doesn't exist.
/// </summary>
public static class DeviceGroupEndpoints
{
  public static IEndpointRouteBuilder MapDeviceGroupEndpoints(this IEndpointRouteBuilder endpoints)
  {
    var one = $"{Routes.DeviceGroups}/{{id:guid}}";

    endpoints.MapGet(Routes.DeviceGroups, async (Guid? tenantId, ClaimsPrincipal caller, StealthDeskDb db, CancellationToken cancellationToken) =>
      {
        if (Tenant(caller, tenantId) is not { } tenant)
        {
          return Results.Forbid();
        }

        var groups = await InTenant(db, tenant)
          .OrderBy(x => x.Name)
          .Select(x => new DeviceGroupSummary(x.Id, x.Name, x.Description, x.CreatedAt, x.Members.Count))
          .ToListAsync(cancellationToken);
        return Results.Ok(new DeviceGroupList { Items = groups });
      })
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantDeviceGroupsRead));

    endpoints.MapGet(one, async (Guid id, Guid? tenantId, ClaimsPrincipal caller, StealthDeskDb db, CancellationToken cancellationToken) =>
        Tenant(caller, tenantId) is not { } tenant ? Results.Forbid()
        : await DetailAsync(db, id, tenant, cancellationToken) is { } detail ? Results.Ok(detail)
        : Results.NotFound())
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantDeviceGroupsRead));

    endpoints.MapPost(Routes.DeviceGroups, async (
        Guid? tenantId,
        DeviceGroupRequest request,
        ClaimsPrincipal caller,
        StealthDeskDb db,
        IAuthorizationChangeFactory changes,
        TimeProvider clock,
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

        var group = new DeviceGroupRecord { Id = Guid.NewGuid(), TenantId = tenant, Name = name, Description = Clean(request.Description), CreatedAt = clock.GetUtcNow() };
        db.DeviceGroups.Add(group);
        db.AuthorizationChanges.Add(changes.Create(
          AuthorizationChangeActions.DeviceGroupCreated, Principal.From(caller), AuthorizationChangeTargets.DeviceGroup, group.Id, tenant,
          after: new DeviceGroupSnapshot(group.Name, group.Description)));
        if (await SaveAsync(db, cancellationToken) is { } conflict)
        {
          return conflict;
        }

        return Results.Created($"{Routes.DeviceGroup(group.Id)}?tenantId={tenant}", await DetailAsync(db, group.Id, tenant, cancellationToken));
      })
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantDeviceGroupsWrite));

    endpoints.MapPut(one, async (
        Guid id,
        Guid? tenantId,
        DeviceGroupRequest request,
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

        var before = new DeviceGroupSnapshot(group.Name, group.Description);
        group.Name = name;
        group.Description = Clean(request.Description);
        db.AuthorizationChanges.Add(changes.Create(
          AuthorizationChangeActions.DeviceGroupUpdated, Principal.From(caller), AuthorizationChangeTargets.DeviceGroup, id, tenant,
          before, new DeviceGroupSnapshot(group.Name, group.Description)));
        if (await SaveAsync(db, cancellationToken) is { } conflict)
        {
          return conflict;
        }

        return Results.Ok(await DetailAsync(db, id, tenant, cancellationToken));
      })
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantDeviceGroupsWrite));

    // Permissions granted on the group go with it; its devices stay, in no group or in their others.
    endpoints.MapDelete(one, async (
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
          .Where(x => x.ScopeKind == PermissionScopeKind.DeviceGroup && x.ScopeId == id)
          .ToListAsync(cancellationToken));
        db.DeviceGroupMembers.RemoveRange(group.Members);
        db.DeviceGroups.Remove(group);
        db.AuthorizationChanges.Add(changes.Create(
          AuthorizationChangeActions.DeviceGroupDeleted, Principal.From(caller), AuthorizationChangeTargets.DeviceGroup, id, tenant,
          before: new DeviceGroupSnapshot(group.Name, group.Description)));
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
      })
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantDeviceGroupsWrite));

    endpoints.MapPost($"{one}/members", (
        Guid id,
        Guid? tenantId,
        DeviceGroupMembersRequest request,
        ClaimsPrincipal caller,
        IAuthorizationService authorization,
        StealthDeskDb db,
        IAuthorizationChangeFactory changes,
        CancellationToken cancellationToken) =>
      ChangeMembersAsync(id, tenantId, request, caller, authorization, db, changes, add: true, cancellationToken))
      .RequireAuthorization()
      .ChecksPermission(PermissionNames.DeviceGroupAssignDevices);

    endpoints.MapDelete($"{one}/members", (
        Guid id,
        Guid? tenantId,
        [Microsoft.AspNetCore.Mvc.FromBody] DeviceGroupMembersRequest request,
        ClaimsPrincipal caller,
        IAuthorizationService authorization,
        StealthDeskDb db,
        IAuthorizationChangeFactory changes,
        CancellationToken cancellationToken) =>
      ChangeMembersAsync(id, tenantId, request, caller, authorization, db, changes, add: false, cancellationToken))
      .RequireAuthorization()
      .ChecksPermission(PermissionNames.DeviceGroupAssignDevices);

    return endpoints;
  }

  // The permission is checked on each group touched, so a grant for one group lets its holder manage only that one;
  // moving needs it on both.
  private static async Task<IResult> ChangeMembersAsync(
    Guid id,
    Guid? tenantId,
    DeviceGroupMembersRequest request,
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

    var from = add ? request.FromGroupId : null;
    if (from == id)
    {
      return Results.Problem("Moving devices needs another group to move them from.", statusCode: StatusCodes.Status400BadRequest);
    }

    foreach (var touched in from is { } source ? [id, source] : new[] { id })
    {
      if (!(await authorization.AuthorizeAsync(caller, Resource.DeviceGroup(touched, tenant), PermissionPolicies.For(PermissionNames.DeviceGroupAssignDevices))).Succeeded)
      {
        return Results.Forbid();
      }
    }

    if (await InTenant(db, tenant).Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } group)
    {
      return Results.NotFound();
    }

    DeviceGroupRecord? fromGroup = null;
    if (from is { } fromId)
    {
      fromGroup = await InTenant(db, tenant).Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == fromId, cancellationToken);
      if (fromGroup is null)
      {
        return Results.NotFound();
      }
    }

    var asked = (request.DeviceIds ?? []).Distinct().ToList();
    if (add && asked.Count > 0
      && await db.Devices.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenant && asked.Contains(x.Id), cancellationToken) != asked.Count)
    {
      return Results.Problem("One or more devices were not found in this tenant.", statusCode: StatusCodes.Status400BadRequest);
    }

    var current = group.Members.Select(x => x.DeviceId).ToHashSet();
    int changed;
    if (add)
    {
      var joining = asked.Where(x => !current.Contains(x)).ToList();
      db.DeviceGroupMembers.AddRange(joining.Select(x => new DeviceGroupMemberRecord { DeviceGroupId = id, DeviceId = x }));
      var leaving = fromGroup?.Members.Where(x => asked.Contains(x.DeviceId)).ToList() ?? [];
      db.DeviceGroupMembers.RemoveRange(leaving);
      changed = fromGroup is null ? joining.Count : asked.Count(x => !current.Contains(x) || leaving.Any(l => l.DeviceId == x));
    }
    else
    {
      var leaving = group.Members.Where(x => asked.Contains(x.DeviceId)).ToList();
      db.DeviceGroupMembers.RemoveRange(leaving);
      changed = leaving.Count;
    }

    if (changed > 0)
    {
      var action = fromGroup is not null ? AuthorizationChangeActions.DeviceGroupMembersMoved
        : add ? AuthorizationChangeActions.DeviceGroupMembersAdded
        : AuthorizationChangeActions.DeviceGroupMembersRemoved;
      db.AuthorizationChanges.Add(changes.Create(
        action, Principal.From(caller), AuthorizationChangeTargets.DeviceGroup, id, tenant,
        after: new DeviceGroupMembershipChange(changed, fromGroup?.Id)));
      await db.SaveChangesAsync(cancellationToken);
    }

    return Results.NoContent();
  }

  private static async Task<DeviceGroupDetail?> DetailAsync(StealthDeskDb db, Guid id, Guid tenant, CancellationToken cancellationToken)
  {
    var group = await InTenant(db, tenant).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (group is null)
    {
      return null;
    }

    var devices = await db.DeviceGroupMembers
      .Where(x => x.DeviceGroupId == id)
      .Select(x => new DeviceGroupDevice(x.DeviceId, x.Device!.Name, x.Device.IsOnline, x.Device.LastSeen))
      .ToListAsync(cancellationToken);
    return new DeviceGroupDetail(group.Id, group.Name, group.Description, group.CreatedAt, [.. devices.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)]);
  }

  // The check before saving can't stop two identical names saved at the same instant; the unique index can.
  private static async Task<IResult?> SaveAsync(StealthDeskDb db, CancellationToken cancellationToken)
  {
    try
    {
      await db.SaveChangesAsync(cancellationToken);
      return null;
    }
    catch (DbUpdateException)
    {
      return NameTaken();
    }
  }

  private static IResult? Invalid(DeviceGroupRequest request)
  {
    var errors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > DeviceGroupRequest.NameMax)
    {
      errors[nameof(request.Name)] = [$"Enter a name of at most {DeviceGroupRequest.NameMax} characters."];
    }

    if (request.Description?.Length > DeviceGroupRequest.DescriptionMax)
    {
      errors[nameof(request.Description)] = [$"Use at most {DeviceGroupRequest.DescriptionMax} characters."];
    }

    return errors.Count == 0 ? null : Results.ValidationProblem(errors);
  }

  private static IResult NameTaken() => Results.Problem("A device group with that name already exists.", statusCode: StatusCodes.Status409Conflict);

  private static string? Clean(string? description) => string.IsNullOrWhiteSpace(description) ? null : description.Trim();

  private static Guid? Tenant(ClaimsPrincipal caller, Guid? requested) => Principal.From(caller)?.TenantFor(requested ?? Guid.Empty);

  private static IQueryable<DeviceGroupRecord> InTenant(StealthDeskDb db, Guid tenant) =>
    db.DeviceGroups.IgnoreQueryFilters().Where(x => x.TenantId == tenant);
}
