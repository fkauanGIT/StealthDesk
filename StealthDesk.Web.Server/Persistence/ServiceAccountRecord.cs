using StealthDesk.Contracts.ServiceAccounts;

namespace StealthDesk.Web.Server.Persistence;

/// <summary>
/// An account for automation, with no person behind it. A tenant's account belongs to that tenant; a server account
/// belongs to none and works across tenants.
/// </summary>
public class ServiceAccountRecord
{
  public const int NameMax = CreateServiceAccountRequest.NameMax;
  public const int DescriptionMax = CreateServiceAccountRequest.DescriptionMax;

  public Guid Id { get; set; }

  public ServiceAccountKind Kind { get; set; }

  /// <summary>The owning tenant; none for a server account, always one for a tenant account.</summary>
  public Guid? TenantId { get; set; }
  public TenantRecord? Tenant { get; set; }

  public string Name { get; set; } = string.Empty;
  public string? Description { get; set; }
  public bool IsEnabled { get; set; } = true;

  /// <summary>Chosen when a server account is created and never inferred; a tenant account is always restricted.</summary>
  public ServiceAccountAccessMode AccessMode { get; set; }

  public DateTimeOffset CreatedAt { get; set; }

  public List<ServiceAccountCredentialRecord> Credentials { get; set; } = [];
}

/// <summary>One of a service account's keys. Only a hash of its secret is kept, like a password.</summary>
public class ServiceAccountCredentialRecord
{
  public const int NameMax = CreateServiceAccountCredentialRequest.NameMax;
  public const int HashMax = 256;

  public Guid Id { get; set; }

  public Guid ServiceAccountId { get; set; }
  public ServiceAccountRecord? ServiceAccount { get; set; }

  public string Name { get; set; } = string.Empty;
  public string HashedSecret { get; set; } = string.Empty;

  public DateTimeOffset CreatedAt { get; set; }

  /// <summary>None for a credential that never expires.</summary>
  public DateTimeOffset? ExpiresAt { get; set; }

  public DateTimeOffset? RevokedAt { get; set; }
  public DateTimeOffset? LastUsed { get; set; }

  /// <summary>Revoked or expired: it never works again, and may be deleted.</summary>
  public bool IsDead(DateTimeOffset now) => RevokedAt is not null || ExpiresAt <= now;
}
