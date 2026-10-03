using Microsoft.AspNetCore.Identity;

namespace StealthDesk.Web.Server.Persistence;

/// <summary>A person who signs in to the dashboard. Identity keeps the credentials; we add what StealthDesk needs.</summary>
public class UserRecord : IdentityUser<Guid>
{
  public Guid TenantId { get; set; }
  public TenantRecord? Tenant { get; set; }
  public AccountType AccountType { get; set; }
  public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
  public DateTimeOffset? LastSignIn { get; set; }
  public bool IsOnline { get; set; }

  /// <summary>Set for accounts created with a temporary password; cleared once the user picks their own.</summary>
  public bool MustChangePassword { get; set; }
}

public enum AccountType
{
  /// <summary>Registered or invited into a tenant.</summary>
  Member,

  /// <summary>Signed in from outside the tenant for a single purpose, without a full account.</summary>
  External,
}
