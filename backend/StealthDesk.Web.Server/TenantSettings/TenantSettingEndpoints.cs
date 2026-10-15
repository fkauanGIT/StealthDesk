using System.Security.Claims;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.TenantSettings;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.TenantSettings;

/// <summary>The settings of the tenant acted in (<c>?tenantId=</c> for server-wide principals).</summary>
public static class TenantSettingEndpoints
{
  public static IEndpointRouteBuilder MapTenantSettingEndpoints(this IEndpointRouteBuilder endpoints)
  {
    var read = PermissionPolicies.For(PermissionNames.TenantSettingsRead);
    var write = PermissionPolicies.For(PermissionNames.TenantSettingsWrite);
    var one = $"{Routes.TenantSettings}/{{name}}";

    endpoints.MapGet(Routes.TenantSettings, (Guid? tenantId, ClaimsPrincipal caller, TenantSettingService settings, CancellationToken cancellationToken) =>
        As(caller, tenantId, tenant => settings.ListAsync(tenant, cancellationToken)))
      .RequireAuthorization(read);

    endpoints.MapGet(one, (string name, Guid? tenantId, ClaimsPrincipal caller, TenantSettingService settings, CancellationToken cancellationToken) =>
        As(caller, tenantId, tenant => settings.GetAsync(tenant, name, cancellationToken)))
      .RequireAuthorization(read);

    endpoints.MapPut(one, (string name, Guid? tenantId, SetTenantSettingRequest request, ClaimsPrincipal caller, TenantSettingService settings, CancellationToken cancellationToken) =>
        As(caller, tenantId, tenant => settings.SetAsync(tenant, name, request.Value, cancellationToken)))
      .RequireAuthorization(write);

    endpoints.MapPut(Routes.TenantSettings, (Guid? tenantId, SetTenantSettingsRequest request, ClaimsPrincipal caller, TenantSettingService settings, CancellationToken cancellationToken) =>
        As(caller, tenantId, tenant => settings.SetManyAsync(tenant, request.Values ?? new Dictionary<string, string?>(), cancellationToken)))
      .RequireAuthorization(write);

    endpoints.MapDelete(one, (string name, Guid? tenantId, ClaimsPrincipal caller, TenantSettingService settings, CancellationToken cancellationToken) =>
        As(caller, tenantId, tenant => settings.DeleteAsync(tenant, name, cancellationToken)))
      .RequireAuthorization(write);

    // Whoever installs agents needs the resolved options, not the right to read every setting.
    endpoints.MapGet(Routes.DeploymentOptions, (Guid? tenantId, HttpContext http, ClaimsPrincipal caller, TenantSettingService settings, CancellationToken cancellationToken) =>
        As(caller, tenantId, tenant => settings.DeploymentOptionsAsync(tenant, http.Request.Host.Host, cancellationToken)))
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.AgentInstall));

    return endpoints;
  }

  private static async Task<IResult> As(ClaimsPrincipal caller, Guid? tenantId, Func<Guid, Task<IResult>> action) =>
    Principal.From(caller) is { } actor && actor.TenantFor(tenantId ?? Guid.Empty) is { } tenant
      ? await action(tenant)
      : Results.Forbid();
}
