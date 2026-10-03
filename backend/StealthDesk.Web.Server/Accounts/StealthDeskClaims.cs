using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace StealthDesk.Web.Server.Accounts;

public static class StealthDeskClaims
{
  public const string TenantId = "stealthdesk:tenant_id";

  public static Guid? GetTenantId(this ClaimsPrincipal principal) =>
    Guid.TryParse(principal.FindFirstValue(TenantId), out var tenantId) ? tenantId : null;
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
