using System.Security.Claims;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.ServiceAccounts;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.ServiceAccounts;

/// <summary>
/// A tenant's service accounts (<c>service-account.*</c>) and the server's (<c>server.service-accounts.*</c>), with
/// the same routes under each: the accounts, and their credentials.
/// </summary>
public static class ServiceAccountEndpoints
{
  public static IEndpointRouteBuilder MapServiceAccountEndpoints(this IEndpointRouteBuilder endpoints)
  {
    // A tenant's accounts are those of the tenant acted in.
    Map(
      endpoints,
      Routes.ServiceAccounts,
      (actor, tenantId) => actor.TenantFor(tenantId ?? Guid.Empty) is { } tenant ? new Owner(tenant) : null,
      PermissionNames.ServiceAccountRead,
      PermissionNames.ServiceAccountWrite,
      PermissionNames.ServiceAccountRotateCredentials);

    Map(
      endpoints,
      Routes.ServerServiceAccounts,
      (_, _) => new Owner(null),
      PermissionNames.ServerServiceAccountsRead,
      PermissionNames.ServerServiceAccountsWrite,
      PermissionNames.ServerServiceAccountsRotateCredentials);

    return endpoints;
  }

  /// <summary>Whose accounts a request works on: a tenant's, or the server's.</summary>
  private sealed record Owner(Guid? Tenant);

  private static void Map(
    IEndpointRouteBuilder endpoints,
    string accounts,
    Func<Principal, Guid?, Owner?> ownerOf,
    string read,
    string write,
    string rotate)
  {
    var one = $"{accounts}/{{id:guid}}";
    var credentials = $"{one}/credentials";
    var credential = $"{credentials}/{{credentialId:guid}}";

    async Task<IResult> As(ClaimsPrincipal caller, Guid? tenantId, Func<Principal, Guid?, Task<IResult>> action) =>
      Principal.From(caller) is { } actor && ownerOf(actor, tenantId) is { } owner
        ? await action(actor, owner.Tenant)
        : Results.Forbid();

    endpoints.MapGet(accounts, (Guid? tenantId, ClaimsPrincipal caller, ServiceAccountService service, CancellationToken cancellationToken) =>
        As(caller, tenantId, async (_, owner) => Results.Ok(new ServiceAccountList { Items = await service.ListAsync(owner, cancellationToken) })))
      .RequireAuthorization(PermissionPolicies.For(read));

    endpoints.MapGet(one, (Guid id, Guid? tenantId, ClaimsPrincipal caller, ServiceAccountService service, CancellationToken cancellationToken) =>
        As(caller, tenantId, (_, owner) => service.GetAsync(owner, id, cancellationToken)))
      .RequireAuthorization(PermissionPolicies.For(read));

    endpoints.MapPost(accounts, (Guid? tenantId, CreateServiceAccountRequest request, ClaimsPrincipal caller, ServiceAccountService service, CancellationToken cancellationToken) =>
        As(caller, tenantId, (actor, owner) => service.CreateAsync(actor, owner, request, cancellationToken)))
      .RequireAuthorization(PermissionPolicies.For(write));

    endpoints.MapPut(one, (Guid id, Guid? tenantId, UpdateServiceAccountRequest request, ClaimsPrincipal caller, ServiceAccountService service, CancellationToken cancellationToken) =>
        As(caller, tenantId, (actor, owner) => service.UpdateAsync(actor, owner, id, request, cancellationToken)))
      .RequireAuthorization(PermissionPolicies.For(write));

    endpoints.MapDelete(one, (Guid id, Guid? tenantId, ClaimsPrincipal caller, ServiceAccountService service, CancellationToken cancellationToken) =>
        As(caller, tenantId, (actor, owner) => service.DeleteAsync(actor, owner, id, cancellationToken)))
      .RequireAuthorization(PermissionPolicies.For(write));

    endpoints.MapPost(credentials, (Guid id, Guid? tenantId, CreateServiceAccountCredentialRequest request, ClaimsPrincipal caller, ServiceAccountService service, CancellationToken cancellationToken) =>
        As(caller, tenantId, (actor, owner) => service.AddCredentialAsync(actor, owner, id, request, cancellationToken)))
      .RequireAuthorization(PermissionPolicies.For(rotate));

    endpoints.MapDelete(credential, (Guid id, Guid credentialId, Guid? tenantId, ClaimsPrincipal caller, ServiceAccountService service, CancellationToken cancellationToken) =>
        As(caller, tenantId, (actor, owner) => service.RevokeCredentialAsync(actor, owner, id, credentialId, cancellationToken)))
      .RequireAuthorization(PermissionPolicies.For(rotate));

    endpoints.MapDelete($"{credential}/purge", (Guid id, Guid credentialId, Guid? tenantId, ClaimsPrincipal caller, ServiceAccountService service, CancellationToken cancellationToken) =>
        As(caller, tenantId, (actor, owner) => service.PurgeCredentialAsync(actor, owner, id, credentialId, cancellationToken)))
      .RequireAuthorization(PermissionPolicies.For(rotate));
  }
}
