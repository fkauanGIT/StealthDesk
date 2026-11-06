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

  /// <summary>Whether the user at a device is told, on its screen, when this user starts a remote control session.</summary>
  public bool NotifyOnRemoteControl { get; init; }

  /// <summary>True when the tenant has its own choice for <see cref="NotifyOnRemoteControl"/>, which then can't be changed here.</summary>
  public bool NotifyOnRemoteControlOverriddenByTenant { get; init; }
}

public sealed record ProfileUpdate
{
  public string? PhoneNumber { get; init; }
}

/// <summary>The signed-in user's own choice of whether the device's user is told about their sessions.</summary>
public sealed record NotifyOnRemoteControlUpdate(bool NotifyOnRemoteControl);

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
