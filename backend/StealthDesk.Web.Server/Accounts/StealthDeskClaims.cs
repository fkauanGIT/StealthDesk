using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace StealthDesk.Web.Server.Accounts;

public static class StealthDeskClaims
{
  public const string TenantId = "stealthdesk:tenant_id";

  // Stored with the user until permissions arrive (v0.4), which turn them into permission presets.
  public const string ServerAdministrator = "stealthdesk:server_admin";
  public const string TenantAdministrator = "stealthdesk:tenant_admin";

  public static Guid? GetTenantId(this ClaimsPrincipal principal) =>
    Guid.TryParse(principal.FindFirstValue(TenantId), out var tenantId) ? tenantId : null;

  public static Claim Marker(string type) => new(type, "true");
}

/// <summary>Adds the tenant to every signed-in principal, cookie or bearer, so requests can be scoped by it.</summary>
public sealed class StealthDeskClaimsFactory(UserManager<UserRecord> users, IOptions<IdentityOptions> options)
  : UserClaimsPrincipalFactory<UserRecord>(users, options)
{
  protected override async Task<ClaimsIdentity> GenerateClaimsAsync(UserRecord user)
  {
    var identity = await base.GenerateClaimsAsync(user);
    identity.AddClaim(new Claim(StealthDeskClaims.TenantId, user.TenantId.ToString()));
    return identity;
  }
}
