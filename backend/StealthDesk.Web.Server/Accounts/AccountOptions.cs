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
}
