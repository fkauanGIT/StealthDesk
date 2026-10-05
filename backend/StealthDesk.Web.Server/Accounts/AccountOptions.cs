namespace StealthDesk.Web.Server.Accounts;

/// <summary>Account rules, from the "Accounts" configuration section.</summary>
public sealed class AccountOptions
{
  public const string Section = "Accounts";

  /// <summary>Lets scripts sign in for bearer tokens. Browsers always use the cookie.</summary>
  public bool EnableBearerLogin { get; set; }

  public TimeSpan BearerTokenLifetime { get; set; } = TimeSpan.FromHours(1);

  public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);

  /// <summary>When false, several accounts may share an email address, or have none.</summary>
  public bool RequireUniqueEmail { get; set; } = true;

  /// <summary>Users must confirm their email address before they can sign in. Needs email sending.</summary>
  public bool RequireConfirmedEmail { get; set; }

  /// <summary>Anyone can register at any time; each registration creates its own tenant.</summary>
  public bool EnablePublicRegistration { get; set; }

  /// <summary>
  /// Closes the one-time registration a server allows while it has no users, which makes the first user its
  /// administrator. Independent of <see cref="EnablePublicRegistration"/>.
  /// </summary>
  public bool DisableFirstUserSelfRegistration { get; set; }
}
