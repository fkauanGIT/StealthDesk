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

/// <summary>
/// A server service account and its first credential, created on first startup so automation can configure a new
/// server without anyone signing in. Name, credential id and secret are set together or not at all.
/// </summary>
public sealed class ServiceAccountBootstrapOptions
{
  public const string Section = "Bootstrap";
  public const int MinimumSecretLength = 32;

  public string? ServerServiceAccountName { get; set; }
  public string? ServerServiceAccountDescription { get; set; }

  /// <summary>A fixed id for the account, so automation can know it without asking; a new one when unset.</summary>
  public Guid? ServerServiceAccountId { get; set; }

  /// <summary>The credential's id: the part of the key before the colon.</summary>
  public Guid? ServerServiceAccountCredentialId { get; set; }

  /// <summary>The credential's secret: the part after the colon, at least <see cref="MinimumSecretLength"/> characters.</summary>
  public string? ServerServiceAccountCredentialSecret { get; set; }
}
