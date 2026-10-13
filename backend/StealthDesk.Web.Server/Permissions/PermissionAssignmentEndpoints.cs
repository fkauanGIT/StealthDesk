using System.Security.Claims;
using StealthDesk.Contracts.Permissions;

namespace StealthDesk.Web.Server.Permissions;

/// <summary>
/// Permission assignments of the tenant's users and user groups. Reading needs <c>tenant.permissions.read</c>;
/// changing needs <c>tenant.permissions.write</c>, plus what <see cref="PermissionAssignmentManager"/> checks for the
/// scope and effect. Every route acts in the tenant named by <c>?tenantId=</c>, the caller's own by default.
/// </summary>
public static class PermissionAssignmentEndpoints
{
  public static IEndpointRouteBuilder MapPermissionAssignmentEndpoints(this IEndpointRouteBuilder endpoints)
  {
    var read = PermissionPolicies.For(PermissionNames.TenantPermissionsRead);
    var write = PermissionPolicies.For(PermissionNames.TenantPermissionsWrite);

    endpoints.MapGet(Routes.PermissionAssignments, (
        Guid? tenantId,
        PermissionPrincipalKind principalKind,
        Guid principalId,
        ClaimsPrincipal caller,
        PermissionAssignmentManager manager,
        CancellationToken cancellationToken) =>
      Act(caller, tenantId, (actor, tenant) => manager.ListAsync(actor, tenant, principalKind, principalId, cancellationToken)))
      .RequireAuthorization(read);

    endpoints.MapGet(Routes.PermissionCatalog, () => new PermissionCatalogList
      {
        Items = [.. PermissionCatalog.All.Select(x => new PermissionCatalogEntry(x.Name, x.Category, x.DisplayName, x.Description, x.Scopes))],
      })
      .RequireAuthorization(read);

    endpoints.MapGet(Routes.PermissionPresets, () => new PermissionPresetList
      {
        Items = [.. PermissionPresets.All.Select(x => new PermissionPresetEntry(x.Key, x.Value))],
      })
      .RequireAuthorization(read);

    endpoints.MapPost(Routes.PermissionAssignments, (
        Guid? tenantId,
        CreatePermissionAssignmentRequest request,
        ClaimsPrincipal caller,
        PermissionAssignmentManager manager,
        CancellationToken cancellationToken) =>
      Act(caller, tenantId, (actor, tenant) => manager.CreateAsync(actor, tenant, request, cancellationToken)))
      .RequireAuthorization(write);

    endpoints.MapPut($"{Routes.PermissionAssignments}/{{id:guid}}", (
        Guid id,
        Guid? tenantId,
        UpdatePermissionAssignmentRequest request,
        ClaimsPrincipal caller,
        PermissionAssignmentManager manager,
        CancellationToken cancellationToken) =>
      Act(caller, tenantId, (actor, tenant) => manager.UpdateAsync(actor, tenant, id, request, cancellationToken)))
      .RequireAuthorization(write);

    endpoints.MapDelete($"{Routes.PermissionAssignments}/{{id:guid}}", (
        Guid id,
        Guid? tenantId,
        ClaimsPrincipal caller,
        PermissionAssignmentManager manager,
        CancellationToken cancellationToken) =>
      Act(caller, tenantId, (actor, tenant) => manager.DeleteAsync(actor, tenant, id, cancellationToken)))
      .RequireAuthorization(write);

    endpoints.MapPost(Routes.PermissionAssignmentsBatchDelete, (
        Guid? tenantId,
        DeletePermissionAssignmentsRequest request,
        ClaimsPrincipal caller,
        PermissionAssignmentManager manager,
        CancellationToken cancellationToken) =>
      Act(caller, tenantId, (actor, tenant) => manager.DeleteManyAsync(actor, tenant, request.Ids, cancellationToken)))
      .RequireAuthorization(write);

    endpoints.MapPost(Routes.ApplyPermissionPresets, (
        Guid? tenantId,
        ApplyPermissionPresetsRequest request,
        ClaimsPrincipal caller,
        PermissionAssignmentManager manager,
        CancellationToken cancellationToken) =>
      Act(caller, tenantId, (actor, tenant) => manager.ApplyPresetsAsync(actor, tenant, request, cancellationToken)))
      .RequireAuthorization(write);

    endpoints.MapGet("/api/v1/effective-permissions/{principalId:guid}", (
        Guid principalId,
        Guid? tenantId,
        PermissionPrincipalKind principalKind,
        string permission,
        PermissionScopeKind scopeKind,
        Guid? scopeId,
        ClaimsPrincipal caller,
        PermissionAssignmentManager manager,
        CancellationToken cancellationToken) =>
      Act(caller, tenantId, (_, tenant) => manager.EffectiveAsync(tenant, principalKind, principalId, permission, scopeKind, scopeId, cancellationToken)))
      .RequireAuthorization(read);

    return endpoints;
  }

  private static async Task<IResult> Act(ClaimsPrincipal caller, Guid? tenantId, Func<Principal, Guid, Task<IResult>> action) =>
    Principal.From(caller) is { } actor && actor.TenantFor(tenantId ?? Guid.Empty) is { } tenant
      ? await action(actor, tenant)
      : Results.Forbid();
}
