namespace StealthDesk.Contracts.Accounts;

/// <summary>A sign-in provider the server is configured for, e.g. Microsoft or GitHub.</summary>
public sealed record ExternalProvider
{
  /// <summary>The name in the API's paths, e.g. "GitHub".</summary>
  public string Scheme { get; init; } = string.Empty;

  public string DisplayName { get; init; } = string.Empty;
}

/// <summary>Who just came back from a provider without an account here yet.</summary>
public sealed record PendingExternalLogin
{
  public string ProviderDisplayName { get; init; } = string.Empty;

  /// <summary>The email the provider shared, if any.</summary>
  public string? Email { get; init; }
}

public sealed record ExternalRegistration
{
  public string Email { get; init; } = string.Empty;
}

public sealed record ExternalRegistrationResult
{
  /// <summary>False when the account must confirm its email first.</summary>
  public bool SignedIn { get; init; }
}

public sealed record LinkedLogin
{
  public string Provider { get; init; } = string.Empty;
  public string DisplayName { get; init; } = string.Empty;
  public string ProviderKey { get; init; } = string.Empty;
}

/// <summary>The providers linked to the signed-in account and those it could still link.</summary>
public sealed record LinkedLogins
{
  public IReadOnlyList<LinkedLogin> Linked { get; init; } = [];
  public IReadOnlyList<ExternalProvider> Available { get; init; } = [];

  /// <summary>False when one linked login is the account's only way to sign in.</summary>
  public bool CanRemove { get; init; }
}
