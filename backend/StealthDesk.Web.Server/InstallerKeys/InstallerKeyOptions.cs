namespace StealthDesk.Web.Server.InstallerKeys;

public sealed class InstallerKeyOptions
{
  public const string Section = "InstallerKeys";

  /// <summary>
  /// Installs older than this are left out of a key's history and removed in the background. Zero or less keeps
  /// every install.
  /// </summary>
  public int UsageHistoryDays { get; set; } = 90;
}
