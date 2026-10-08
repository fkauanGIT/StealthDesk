using Microsoft.AspNetCore.Authorization;
using StealthDesk.Contracts.Permissions;

namespace StealthDesk.Web.Server.Permissions;

/// <summary>A policy's requirement: this permission, on the resource the check names or the principal's tenant.</summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
  public string Permission { get; } = permission;
}

/// <summary>
/// Connects ASP.NET Core authorization to the evaluator. A check may pass the resource (a <see cref="Resource"/>
/// or a device); without one, a server permission is checked on the server and any other on the principal's tenant.
/// </summary>
public sealed class PermissionRequirementHandler(IPermissionEvaluator evaluator, IHttpContextAccessor http, ILogger<PermissionRequirementHandler> logger)
  : AuthorizationHandler<PermissionRequirement>
{
  protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
  {
    if (Principal.From(context.User) is not { } principal)
    {
      context.Fail(new AuthorizationFailureReason(this, "The request doesn't say who is asking."));
      return;
    }

    if (ResourceOf(context.Resource, requirement.Permission, principal) is not { } resource)
    {
      context.Fail(new AuthorizationFailureReason(this, $"No resource to check '{requirement.Permission}' on."));
      return;
    }

    var decision = await evaluator.EvaluateAsync(principal, requirement.Permission, resource, http.HttpContext?.RequestAborted ?? default);
    if (decision.Allowed)
    {
      context.Succeed(requirement);
      return;
    }

    logger.LogDebug("{Permission} denied to {Kind} {Id} on {Resource}: {Reason}", requirement.Permission, principal.Kind, principal.Id, resource, decision.Reason);
    context.Fail(new AuthorizationFailureReason(this, decision.Reason));
  }

  // A device permission needs the device: checked without one, it fails closed instead of guessing.
  private static Resource? ResourceOf(object? checkedResource, string permission, Principal principal) => checkedResource switch
  {
    Resource resource => resource,
    DeviceRecord device => Resource.Device(device),
    _ when PermissionCatalog.Find(permission) is { AllowsTenantScope: false } => Resource.Server,
    _ when principal.TenantId is { } tenantId => Resource.Tenant(tenantId),
    _ => null,
  };
}

public static class PermissionSetup
{
  /// <summary>The evaluator, and one policy per permission in the catalog.</summary>
  public static IServiceCollection AddPermissions(this IServiceCollection services)
  {
    services.AddScoped<IPermissionEvaluator, PermissionEvaluator>();
    services.AddScoped<IAuthorizationHandler, PermissionRequirementHandler>();
    services.AddScoped<PermissionSeeder>();
    services.AddAuthorizationBuilder().AddPermissionPolicies();
    return services;
  }

  private static AuthorizationBuilder AddPermissionPolicies(this AuthorizationBuilder builder)
  {
    foreach (var permission in PermissionCatalog.All)
    {
      builder.AddPolicy(PermissionPolicies.For(permission.Name), policy => policy
        .RequireAuthenticatedUser()
        .AddRequirements(new PermissionRequirement(permission.Name)));
    }

    return builder;
  }
}
