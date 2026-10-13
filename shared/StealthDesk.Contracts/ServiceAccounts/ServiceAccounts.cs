namespace StealthDesk.Contracts.ServiceAccounts;

/// <summary>Whose a service account is: one tenant's, or the whole server's.</summary>
public enum ServiceAccountKind
{
  Tenant = 0,
  Server = 1,
}

/// <summary>How a server service account's permissions are decided; tenant accounts are always restricted.</summary>
public enum ServiceAccountAccessMode
{
  /// <summary>Only what it was granted.</summary>
  Restricted = 0,

  /// <summary>Everything on the server, whatever it was granted.</summary>
  Unrestricted = 1,
}

/// <summary>A credential as listed: never its secret.</summary>
/// <param name="ExpiresAt">None for a credential that never expires.</param>
/// <param name="RevokedAt">When it stopped working on purpose; none while it still works.</param>
public sealed record ServiceAccountCredential(
  Guid Id,
  string Name,
  DateTimeOffset CreatedAt,
  DateTimeOffset? ExpiresAt,
  DateTimeOffset? RevokedAt,
  DateTimeOffset? LastUsed);

public sealed record ServiceAccount(
  Guid Id,
  string Name,
  string? Description,
  ServiceAccountKind Kind,
  bool IsEnabled,
  ServiceAccountAccessMode AccessMode,
  DateTimeOffset CreatedAt,
  IReadOnlyList<ServiceAccountCredential> Credentials);

public sealed class ServiceAccountList
{
  public IReadOnlyList<ServiceAccount> Items { get; init; } = [];
}

/// <param name="AccessMode">For a server account; a tenant account is always restricted.</param>
public sealed record CreateServiceAccountRequest(
  string Name,
  string? Description = null,
  ServiceAccountAccessMode AccessMode = ServiceAccountAccessMode.Restricted)
{
  public const int NameMax = 100;
  public const int DescriptionMax = 500;
}

/// <summary>The access mode is chosen at creation and never changes.</summary>
public sealed record UpdateServiceAccountRequest(string Name, string? Description, bool IsEnabled);

/// <param name="ExpiresAt">Any time in the future; none for a credential that never expires.</param>
public sealed record CreateServiceAccountCredentialRequest(string Name, DateTimeOffset? ExpiresAt)
{
  public const int NameMax = 100;
}

/// <summary>The credential as created, with its key: shown this once and never again.</summary>
/// <param name="ApiKey">What scripts send in the <see cref="ServiceAccountHeader.Name"/> header.</param>
public sealed record CreatedServiceAccountCredential(ServiceAccountCredential Credential, string ApiKey);

public static class ServiceAccountHeader
{
  public const string Name = "x-api-key";
}
