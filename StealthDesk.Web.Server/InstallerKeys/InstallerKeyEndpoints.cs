using System.Security.Claims;
using StealthDesk.Contracts.InstallerKeys;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.InstallerKeys;

/// <summary>The installer keys of the tenant acted in (<c>?tenantId=</c> for server-wide principals).</summary>
public static class InstallerKeyEndpoints
{
  public static IEndpointRouteBuilder MapInstallerKeyEndpoints(this IEndpointRouteBuilder endpoints)
  {
    var read = PermissionPolicies.For(PermissionNames.InstallerKeyRead);
    var write = PermissionPolicies.For(PermissionNames.InstallerKeyWrite);
    var one = $"{Routes.InstallerKeys}/{{id:guid}}";

    endpoints.MapGet(Routes.InstallerKeys, (Guid? tenantId, ClaimsPrincipal caller, InstallerKeyService keys, CancellationToken cancellationToken) =>
        As(caller, tenantId, async (actor, tenant) => Results.Ok(new InstallerKeyList { Items = await keys.ListAsync(actor, tenant, cancellationToken) })))
      .RequireAuthorization(read);

    endpoints.MapPost(Routes.InstallerKeys, (Guid? tenantId, CreateInstallerKeyRequest request, ClaimsPrincipal caller, InstallerKeyService keys, CancellationToken cancellationToken) =>
        As(caller, tenantId, (actor, tenant) => keys.CreateAsync(actor, tenant, request, cancellationToken)))
      .RequireAuthorization(write);

    endpoints.MapPut(one, (Guid id, Guid? tenantId, RenameInstallerKeyRequest request, ClaimsPrincipal caller, InstallerKeyService keys, CancellationToken cancellationToken) =>
        As(caller, tenantId, (actor, tenant) => keys.RenameAsync(actor, tenant, id, request.Name, cancellationToken)))
      .RequireAuthorization(write);

    endpoints.MapDelete(one, (Guid id, Guid? tenantId, ClaimsPrincipal caller, InstallerKeyService keys, CancellationToken cancellationToken) =>
        As(caller, tenantId, (actor, tenant) => keys.DeleteAsync(actor, tenant, id, cancellationToken)))
      .RequireAuthorization(write);

    endpoints.MapGet($"{one}/usages", (Guid id, Guid? tenantId, ClaimsPrincipal caller, InstallerKeyService keys, CancellationToken cancellationToken) =>
        As(caller, tenantId, (actor, tenant) => keys.UsagesAsync(actor, tenant, id, cancellationToken)))
      .RequireAuthorization(read);

    return endpoints;
  }

  // Keys always belong to a tenant: a server-wide principal must name one.
  private static async Task<IResult> As(ClaimsPrincipal caller, Guid? tenantId, Func<Principal, Guid, Task<IResult>> action) =>
    Principal.From(caller) is { } actor && actor.TenantFor(tenantId ?? Guid.Empty) is { } tenant
      ? await action(actor, tenant)
      : Results.Forbid();
}
