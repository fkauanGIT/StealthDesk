using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace StealthDesk.Web.Server.Email;

public sealed record EmailMessage(string To, string Subject, string HtmlBody);

/// <summary>Delivers a message: by SMTP, or to the log when sending is disabled.</summary>
public interface IEmailTransport
{
  /// <summary>Never throws: a message that can't be delivered is logged, and the account action goes on.</summary>
  Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

public sealed class EmailTransport(
  IOptionsMonitor<EmailOptions> options,
  IHostEnvironment environment,
  ILogger<EmailTransport> logger) : IEmailTransport
{
  public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
  {
    try
    {
      await DeliverAsync(message, cancellationToken);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // The user can ask for the message again; failing the registration or reset over it helps nobody.
      logger.LogError(ex, "Failed to send \"{Subject}\" to {To}.", message.Subject, message.To);
    }
  }

  private async Task DeliverAsync(EmailMessage message, CancellationToken cancellationToken)
  {
    var settings = options.CurrentValue;

    if (settings.DisableSending)
    {
      if (environment.IsDevelopment())
      {
        // The links as a browser would open them: copied from the HTML they would keep "&amp;" and break.
        logger.LogInformation(
          "Email sending is disabled; this message was not sent.\nTo: {To}\nSubject: {Subject}\nLinks:\n{Links}",
          message.To, message.Subject, string.Join("\n", EmailLinks.In(message.HtmlBody)));
      }
      else
      {
        logger.LogInformation("Email sending is disabled; \"{Subject}\" to {To} was not sent.", message.Subject, message.To);
      }

      return;
    }

    if (settings is not { SmtpHost: { Length: > 0 } host, SenderAddress: { Length: > 0 } sender })
    {
      throw new InvalidOperationException(
        $"Email can't be sent: set {EmailOptions.Section}:SmtpHost and {EmailOptions.Section}:SenderAddress, or {EmailOptions.Section}:DisableSending.");
    }

    var mime = new MimeMessage();
    mime.From.Add(new MailboxAddress(settings.SenderName ?? string.Empty, sender));
    mime.To.Add(MailboxAddress.Parse(message.To));
    mime.Subject = message.Subject;
    mime.Body = new BodyBuilder { HtmlBody = message.HtmlBody }.ToMessageBody();

    using var client = new SmtpClient { CheckCertificateRevocation = settings.SmtpCheckCertificateRevocation };
    if (!string.IsNullOrWhiteSpace(settings.SmtpLocalDomain))
    {
      client.LocalDomain = settings.SmtpLocalDomain;
    }

    // Port 465 speaks TLS from the start; others upgrade with STARTTLS when the server offers it.
    var security = settings.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
    await client.ConnectAsync(host, settings.SmtpPort, security, cancellationToken);

    if (!string.IsNullOrWhiteSpace(settings.SmtpUserName))
    {
      await client.AuthenticateAsync(settings.SmtpUserName, settings.SmtpPassword ?? string.Empty, cancellationToken);
    }

    await client.SendAsync(mime, cancellationToken);
    await client.DisconnectAsync(quit: true, cancellationToken);
    logger.LogInformation("Sent \"{Subject}\" to {To}.", message.Subject, message.To);
  }
}
