using StealthDesk.Contracts.InstallerKeys;

namespace StealthDesk.Web.Server.Persistence;

/// <summary>
/// A key that lets agents join its tenant. Only a hash of its secret is kept, like a password. It remembers who made
/// it, since the devices it brings in are theirs to allow.
/// </summary>
public class InstallerKeyRecord
{
  public const int NameMax = CreateInstallerKeyRequest.NameMax;
  public const int HashMax = 256;

  public Guid Id { get; set; }

  public Guid TenantId { get; set; }
  public TenantRecord? Tenant { get; set; }

  public string? Name { get; set; }
  public InstallerKeyType Type { get; set; }

  public InstallerKeyCreatorKind CreatorKind { get; set; }

  /// <summary>A user's or a service account's id, as <see cref="CreatorKind"/> says.</summary>
  public Guid CreatorId { get; set; }

  public string HashedSecret { get; set; } = string.Empty;

  public DateTimeOffset CreatedAt { get; set; }

  /// <summary>For a usage-based key; none otherwise.</summary>
  public int? AllowedUses { get; set; }

  /// <summary>None for a persistent key.</summary>
  public DateTimeOffset? ExpiresAt { get; set; }

  public List<InstallerKeyUsageRecord> Usages { get; set; } = [];

  /// <summary>Expired, or a usage-based key with no uses left: it never works again.</summary>
  public bool IsSpent(int usageCount, DateTimeOffset now) => Type switch
  {
    InstallerKeyType.Persistent => false,
    InstallerKeyType.TimeBased => ExpiresAt is not { } expiresAt || expiresAt < now,
    _ => ExpiresAt is not { } expiresAt || expiresAt < now || AllowedUses is not { } allowed || usageCount >= allowed,
  };
}

/// <summary>One install made with a key: the device it brought in, and from where.</summary>
public class InstallerKeyUsageRecord
{
  public const int RemoteAddressMax = 45;

  public Guid Id { get; set; }

  public Guid InstallerKeyId { get; set; }
  public InstallerKeyRecord? InstallerKey { get; set; }

  public Guid TenantId { get; set; }
  public Guid DeviceId { get; set; }
  public string? RemoteAddress { get; set; }
  public DateTimeOffset UsedAt { get; set; }
}
