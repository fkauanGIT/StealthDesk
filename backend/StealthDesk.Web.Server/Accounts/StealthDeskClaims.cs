using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.Accounts;

public static class StealthDeskClaims
{
  public const string TenantId = "stealthdesk:tenant_id";

  public static Guid? GetTenantId(this ClaimsPrincipal principal) =>
    Guid.TryParse(principal.FindFirstValue(TenantId), out var tenantId) ? tenantId : null;
}

/// <summary>
/// Adds the tenant and the permission principal to every signed-in user, cookie or bearer, so requests can be
/// scoped and authorized by them.
/// </summary>
public sealed class StealthDeskClaimsFactory(UserManager<UserRecord> users, IOptions<IdentityOptions> options)
  : UserClaimsPrincipalFactory<UserRecord>(users, options)
{
  protected override async Task<ClaimsIdentity> GenerateClaimsAsync(UserRecord user)
  {
    var identity = await base.GenerateClaimsAsync(user);
    identity.AddClaim(new Claim(StealthDeskClaims.TenantId, user.TenantId.ToString()));
    identity.AddClaim(new Claim(Principal.KindClaim, nameof(PermissionPrincipalKind.User)));
    identity.AddClaim(new Claim(Principal.IdClaim, user.Id.ToString()));
    return identity;
  }
}
