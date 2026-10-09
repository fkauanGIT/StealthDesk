using System.Security.Claims;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.Accounts;

namespace StealthDesk.Web.Server.Permissions;

/// <summary>Who is asking: a user now, and service accounts and tokens as they arrive.</summary>
/// <param name="TenantId">The tenant the principal belongs to; none for a server-wide principal.</param>
public sealed record Principal(PermissionPrincipalKind Kind, Guid Id, Guid? TenantId)
{
  public const string KindClaim = "stealthdesk:principal:kind";
  public const string IdClaim = "stealthdesk:principal:id";

  /// <summary>The principal of a signed-in request, or null when its claims don't say who it is.</summary>
  public static Principal? From(ClaimsPrincipal user)
  {
    if (!Enum.TryParse<PermissionPrincipalKind>(user.FindFirstValue(KindClaim), out var kind)
      || !Guid.TryParse(user.FindFirstValue(IdClaim), out var id))
    {
      return null;
    }

    var tenantId = user.GetTenantId();

    // Only a server-wide principal has no tenant; anyone else without one is refused.
    return tenantId is null && kind is not PermissionPrincipalKind.ServiceAccount ? null : new Principal(kind, id, tenantId);
  }

  /// <summary>
  /// The tenant a request names (<c>?tenantId=</c>), if this principal may act in it: its own tenant, also when the
  /// request names none, or any tenant for a server-wide principal.
  /// </summary>
  public Guid? TenantFor(Guid requested) => TenantId switch
  {
    null => requested == Guid.Empty ? null : requested,
    { } own => requested == Guid.Empty || requested == own ? own : null,
  };
}

/// <summary>What is being accessed: the server, a tenant, a device or a user group.</summary>
/// <param name="TenantId">The tenant it is in; none for the server.</param>
public sealed record Resource(PermissionScopeKind Kind, Guid? Id = null, Guid? TenantId = null)
{
  public static Resource Server { get; } = new(PermissionScopeKind.Server);

  public static Resource Tenant(Guid tenantId) => new(PermissionScopeKind.Tenant, tenantId, tenantId);

  public static Resource Device(Guid deviceId, Guid tenantId) => new(PermissionScopeKind.Device, deviceId, tenantId);

  public static Resource Device(DeviceRecord device) => Device(device.Id, device.TenantId);

  public static Resource UserGroup(Guid groupId, Guid tenantId) => new(PermissionScopeKind.UserGroup, groupId, tenantId);
}

/// <summary>The answer, with why: the rule that decided an allow, or the reason for a deny.</summary>
public sealed record PermissionDecision(bool Allowed, string Reason)
{
  public static PermissionDecision Allow(string reason) => new(true, reason);

  public static PermissionDecision Deny(string reason) => new(false, reason);
}
