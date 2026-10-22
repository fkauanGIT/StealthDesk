namespace StealthDesk.Web.Server.Email;

/// <summary>Outgoing email, from the "Email" configuration section.</summary>
public sealed class EmailOptions
{
  public const string Section = "Email";

  /// <summary>
  /// Nothing is sent. In development the messages are written to the log instead, links included, so accounts
  /// can still be confirmed; elsewhere only the fact that a message was skipped is logged.
  /// </summary>
  public bool DisableSending { get; set; }

  public string? SmtpHost { get; set; }

  public int SmtpPort { get; set; } = 587;

  public string? SmtpUserName { get; set; }

  public string? SmtpPassword { get; set; }

  /// <summary>Name the server introduces itself with (EHLO); some relays require a real domain.</summary>
  public string? SmtpLocalDomain { get; set; }

  public bool SmtpCheckCertificateRevocation { get; set; } = true;

  public string? SenderName { get; set; }

  public string? SenderAddress { get; set; }
}
