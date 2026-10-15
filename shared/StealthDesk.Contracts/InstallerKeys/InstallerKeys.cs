namespace StealthDesk.Contracts.InstallerKeys;

/// <summary>How long an installer key keeps working.</summary>
public enum InstallerKeyType
{
  /// <summary>A number of installs, within <see cref="InstallerKeyRules.UsageBasedLifetime"/> of its creation.</summary>
  UsageBased = 0,

  /// <summary>Any number of installs until it expires.</summary>
  TimeBased = 1,

  /// <summary>Until it is revoked.</summary>
  Persistent = 2,
}

/// <summary>Who made an installer key: the devices it installs are theirs to allow.</summary>
public enum InstallerKeyCreatorKind
{
  User = 0,
  TenantServiceAccount = 1,
  ServerServiceAccount = 2,
}

public static class InstallerKeyRules
{
  /// <summary>A usage-based key stops working this long after it was made, even with uses left.</summary>
  public static readonly TimeSpan UsageBasedLifetime = TimeSpan.FromHours(24);
}

/// <summary>An installer key as listed: never its secret.</summary>
/// <param name="AllowedUses">For a usage-based key; none otherwise.</param>
/// <param name="ExpiresAt">None for a persistent key.</param>
/// <param name="UsageCount">Installs within the usage history kept.</param>
public sealed record InstallerKey(
  Guid Id,
  string? Name,
  InstallerKeyType Type,
  InstallerKeyCreatorKind CreatorKind,
  Guid CreatorId,
  string? CreatorName,
  DateTimeOffset CreatedAt,
  int? AllowedUses,
  DateTimeOffset? ExpiresAt,
  int UsageCount);

public sealed class InstallerKeyList
{
  public IReadOnlyList<InstallerKey> Items { get; init; } = [];
}

/// <summary>One install made with a key.</summary>
public sealed record InstallerKeyUsage(Guid Id, Guid DeviceId, DateTimeOffset UsedAt, string? RemoteAddress);

public sealed class InstallerKeyUsageList
{
  public IReadOnlyList<InstallerKeyUsage> Items { get; init; } = [];
}

/// <param name="AllowedUses">Required for a usage-based key, ignored otherwise.</param>
/// <param name="ExpiresAt">Required for a time-based key, in the future; ignored otherwise.</param>
public sealed record CreateInstallerKeyRequest(
  InstallerKeyType Type,
  string? Name = null,
  int? AllowedUses = null,
  DateTimeOffset? ExpiresAt = null)
{
  public const int NameMax = 200;
}

/// <param name="Name">None clears it.</param>
public sealed record RenameInstallerKeyRequest(string? Name);

/// <summary>The key as created, with what the agent is given: shown this once and never again.</summary>
/// <param name="FullKey">What the agent takes with <c>--installer-key</c>: <c>{id}:{secret}</c>.</param>
public sealed record CreatedInstallerKey(InstallerKey Key, string FullKey);
