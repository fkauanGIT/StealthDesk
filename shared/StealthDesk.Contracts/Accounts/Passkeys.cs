namespace StealthDesk.Contracts.Accounts;

/// <summary>A passkey of the signed-in user, without its key material.</summary>
public sealed record PasskeySummary
{
  /// <summary>The credential ID, base64url-encoded: how the passkey is named in the API's paths.</summary>
  public string Id { get; init; } = string.Empty;

  public string? Name { get; init; }

  public DateTimeOffset CreatedAt { get; init; }

  /// <summary>Synced by the device's provider, e.g. to the user's other devices.</summary>
  public bool IsBackedUp { get; init; }
}

/// <summary>What navigator.credentials.create() or .get() returned, serialized to JSON by the browser.</summary>
public sealed record PasskeyCredential
{
  public string CredentialJson { get; init; } = string.Empty;
}

public sealed record PasskeyRename
{
  public string Name { get; init; } = string.Empty;
}
