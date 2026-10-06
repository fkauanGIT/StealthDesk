namespace StealthDesk.Contracts.Accounts;

/// <summary>The signed-in user's own account, as the account settings pages show it.</summary>
public sealed record AccountProfile
{
  public Guid Id { get; init; }
  public string Email { get; init; } = string.Empty;
  public bool EmailConfirmed { get; init; }
  public string? PhoneNumber { get; init; }

  /// <summary>False when the account only signs in with an external login, so it sets a password instead of changing it.</summary>
  public bool HasPassword { get; init; }
}

public sealed record ProfileUpdate
{
  public string? PhoneNumber { get; init; }
}

public sealed record PasswordChange
{
  public string CurrentPassword { get; init; } = string.Empty;
  public string NewPassword { get; init; } = string.Empty;
}

public sealed record PasswordSet
{
  public string NewPassword { get; init; } = string.Empty;
}

public sealed record AccountDeletion
{
  /// <summary>Required when the account has a password.</summary>
  public string? Password { get; init; }
}
