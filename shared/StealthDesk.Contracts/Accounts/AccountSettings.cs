namespace StealthDesk.Contracts.Accounts;

/// <summary>What the sign-in pages need to know before anyone signs in.</summary>
public sealed record AccountSettings
{
  public bool RegistrationOpen { get; init; }

  /// <summary>How the sign-in pages look; once signed in, the user's own theme applies.</summary>
  public ThemeMode DefaultTheme { get; init; }

  /// <summary>Only the providers the server is configured for; empty means no provider buttons.</summary>
  public IReadOnlyList<ExternalProvider> ExternalProviders { get; init; } = [];
}
