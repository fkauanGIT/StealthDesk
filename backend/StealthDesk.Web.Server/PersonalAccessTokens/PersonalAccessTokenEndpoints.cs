using System.Security.Claims;
using StealthDesk.Contracts.PersonalAccessTokens;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.PersonalAccessTokens;

/// <summary>
/// The signed-in user's own tokens (<c>personal-access-token.self.*</c>), and another user's of the tenant for
/// administrators (<c>personal-access-token.others.*</c>).
/// </summary>
public static class PersonalAccessTokenEndpoints
{
  public static IEndpointRouteBuilder MapPersonalAccessTokenEndpoints(this IEndpointRouteBuilder endpoints)
  {
    var selfRead = PermissionPolicies.For(PermissionNames.PersonalAccessTokenSelfRead);
    var selfWrite = PermissionPolicies.For(PermissionNames.PersonalAccessTokenSelfWrite);
    var othersRead = PermissionPolicies.For(PermissionNames.PersonalAccessTokenOthersRead);
    var othersWrite = PermissionPolicies.For(PermissionNames.PersonalAccessTokenOthersWrite);

    endpoints.MapGet(Routes.PersonalAccessTokens, (ClaimsPrincipal caller, PersonalAccessTokenService tokens, CancellationToken cancellationToken) =>
        Self(caller, async (actor, _) => Results.Ok(new PersonalAccessTokenList { Items = await tokens.ListAsync(actor.Id, cancellationToken) })))
      .RequireAuthorization(selfRead);

    endpoints.MapPost(Routes.PersonalAccessTokens, (CreatePersonalAccessTokenRequest request, ClaimsPrincipal caller, PersonalAccessTokenService tokens, CancellationToken cancellationToken) =>
        Self(caller, (actor, tenant) => tokens.CreateAsync(actor, actor.Id, tenant, request, cancellationToken)))
      .RequireAuthorization(selfWrite);

    endpoints.MapPut($"{Routes.PersonalAccessTokens}/{{id:guid}}", (Guid id, RenamePersonalAccessTokenRequest request, ClaimsPrincipal caller, PersonalAccessTokenService tokens, CancellationToken cancellationToken) =>
        Self(caller, (actor, _) => tokens.RenameAsync(actor.Id, id, request.Name, cancellationToken)))
      .RequireAuthorization(selfWrite);

    endpoints.MapDelete($"{Routes.PersonalAccessTokens}/{{id:guid}}", (Guid id, ClaimsPrincipal caller, PersonalAccessTokenService tokens, CancellationToken cancellationToken) =>
        Self(caller, (actor, tenant) => tokens.DeleteAsync(actor, actor.Id, tenant, id, cancellationToken)))
      .RequireAuthorization(selfWrite);

    var others = $"{Routes.Users}/{{userId:guid}}/personal-access-tokens";

    endpoints.MapGet(others, (Guid userId, Guid? tenantId, ClaimsPrincipal caller, StealthDeskDb db, PersonalAccessTokenService tokens, CancellationToken cancellationToken) =>
        Other(caller, tenantId, userId, db, cancellationToken, async (_, _) => Results.Ok(new PersonalAccessTokenList { Items = await tokens.ListAsync(userId, cancellationToken) })))
      .RequireAuthorization(othersRead);

    endpoints.MapPost(others, (Guid userId, Guid? tenantId, CreatePersonalAccessTokenRequest request, ClaimsPrincipal caller, StealthDeskDb db, PersonalAccessTokenService tokens, CancellationToken cancellationToken) =>
        Other(caller, tenantId, userId, db, cancellationToken, (actor, tenant) => tokens.CreateAsync(actor, userId, tenant, request, cancellationToken)))
      .RequireAuthorization(othersWrite);

    endpoints.MapPut($"{others}/{{id:guid}}", (Guid userId, Guid id, Guid? tenantId, RenamePersonalAccessTokenRequest request, ClaimsPrincipal caller, StealthDeskDb db, PersonalAccessTokenService tokens, CancellationToken cancellationToken) =>
        Other(caller, tenantId, userId, db, cancellationToken, (_, _) => tokens.RenameAsync(userId, id, request.Name, cancellationToken)))
      .RequireAuthorization(othersWrite);

    endpoints.MapDelete($"{others}/{{id:guid}}", (Guid userId, Guid id, Guid? tenantId, ClaimsPrincipal caller, StealthDeskDb db, PersonalAccessTokenService tokens, CancellationToken cancellationToken) =>
        Other(caller, tenantId, userId, db, cancellationToken, (actor, tenant) => tokens.DeleteAsync(actor, userId, tenant, id, cancellationToken)))
      .RequireAuthorization(othersWrite);

    return endpoints;
  }

  // A user's own tokens; nothing else has any.
  private static async Task<IResult> Self(ClaimsPrincipal caller, Func<Principal, Guid, Task<IResult>> action) =>
    Principal.From(caller) is { Kind: PermissionPrincipalKind.User, TenantId: { } tenant } actor
      ? await action(actor, tenant)
      : Results.Forbid();

  // Another user's tokens, only for a user of the tenant acted in; one of another tenant is not found.
  private static async Task<IResult> Other(
    ClaimsPrincipal caller,
    Guid? tenantId,
    Guid userId,
    StealthDeskDb db,
    CancellationToken cancellationToken,
    Func<Principal, Guid, Task<IResult>> action)
  {
    if (Principal.From(caller) is not { } actor || actor.TenantFor(tenantId ?? Guid.Empty) is not { } tenant)
    {
      return Results.Forbid();
    }

    return await db.Users.IgnoreQueryFilters().AnyAsync(x => x.Id == userId && x.TenantId == tenant, cancellationToken)
      ? await action(actor, tenant)
      : Results.NotFound();
  }
}
