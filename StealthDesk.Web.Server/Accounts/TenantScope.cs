namespace StealthDesk.Web.Server.Accounts;

/// <summary>The tenant the current request acts for, which tenant-owned data is filtered by.</summary>
public interface ITenantScope
{
  /// <summary>
  /// The signed-in user's tenant, or null when nobody is signed in: agents, startup and background work see every
  /// tenant, and endpoints that serve users require sign-in.
  /// </summary>
  Guid? TenantId { get; }
}

public sealed class RequestTenantScope(IHttpContextAccessor http) : ITenantScope
{
  public Guid? TenantId => http.HttpContext?.User.GetTenantId();
}

/// <summary>No tenant: everything is visible. For tools that build the database outside a request.</summary>
public sealed class UnscopedTenant : ITenantScope
{
  public static readonly UnscopedTenant Instance = new();

  public Guid? TenantId => null;
}
