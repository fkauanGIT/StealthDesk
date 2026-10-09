using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using StealthDesk.Contracts.Permissions;

namespace StealthDesk.Web.Client.Accounts;

/// <summary>
/// The server's permission policies (<c>permission:device.read</c>, ...) on the web client: met when the user holds
/// the permission, as <c>/me</c> reported it. They only decide what the pages offer; the server checks every request.
/// </summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
  public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
    PermissionPolicies.PermissionOf(policyName) is { } permission
      ? new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(ServerAuthenticationState.PermissionClaim, permission)
        .Build()
      : await base.GetPolicyAsync(policyName);
}

public static class PermissionPolicySetup
{
  public static IServiceCollection AddStealthDeskAuthorization(this IServiceCollection services) =>
    services
      .AddAuthorizationCore()
      .AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
}
