namespace StealthDesk.Contracts.ServerSettings;

public enum AlertSeverity
{
  Info = 0,
  Success = 1,
  Warning = 2,
  Error = 3,
}

/// <summary>A message every signed-in user sees at the top of the app, set by the server's administrators.</summary>
/// <param name="IsDismissible">Users may close it.</param>
/// <param name="ComesBack">Closed, it shows again on the next visit; otherwise it stays closed until it changes.</param>
/// <param name="IsEnabled">Kept but not shown when off.</param>
public sealed record ServerAlert(string Message, AlertSeverity Severity, bool IsDismissible, bool ComesBack, bool IsEnabled)
{
  public const int MessageMax = 500;
}

/// <summary>What sending the test email did.</summary>
/// <param name="To">The administrator's own address: the message never goes anywhere else.</param>
public sealed record TestEmailResult(string To, string Outcome);
