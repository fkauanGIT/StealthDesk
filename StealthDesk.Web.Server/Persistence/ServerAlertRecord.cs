using StealthDesk.Contracts.ServerSettings;

namespace StealthDesk.Web.Server.Persistence;

/// <summary>The server's one alert, kept in a single row whether it is shown or not.</summary>
public class ServerAlertRecord
{
  public const int MessageMax = ServerAlert.MessageMax;

  /// <summary>The only row there is.</summary>
  public static readonly Guid SingletonId = Guid.Parse("5e7e0a1e-0000-4000-8000-000000000001");

  public Guid Id { get; set; }
  public string Message { get; set; } = string.Empty;
  public AlertSeverity Severity { get; set; }
  public bool IsDismissible { get; set; }
  public bool ComesBack { get; set; }
  public bool IsEnabled { get; set; }
  public DateTimeOffset UpdatedAt { get; set; }
}
