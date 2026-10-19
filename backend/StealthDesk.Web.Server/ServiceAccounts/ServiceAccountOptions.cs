namespace StealthDesk.Web.Server.ServiceAccounts;

public sealed class ServiceAccountOptions
{
  public const string Section = "ServiceAccounts";

  /// <summary>Failed sign-ins allowed per address, and per credential, before more are refused for a while.</summary>
  public int AuthFailureLimit { get; set; } = 5;

  public int AuthFailureWindowMinutes { get; set; } = 5;

  /// <summary>Revoked and expired credentials are deleted this long after they stopped working. Zero or less keeps them.</summary>
  public int CredentialCleanupAfterDays { get; set; } = 30;
}
