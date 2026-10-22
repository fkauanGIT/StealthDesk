using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Web.Server.Accounts;

namespace StealthDesk.Web.Server.Email;

public static class EmailSetup
{
  public static WebApplicationBuilder AddStealthDeskEmail(this WebApplicationBuilder builder)
  {
    builder.Services.AddOptions<EmailOptions>()
      .Bind(builder.Configuration.GetSection(EmailOptions.Section))
      .Validate(x => x.SmtpPort is > 0 and <= 65535, $"{EmailOptions.Section}:{nameof(EmailOptions.SmtpPort)} must be a port number from 1 to 65535.")
      .Validate(
        x => string.IsNullOrWhiteSpace(x.SenderAddress) || MailAddress.TryCreate(x.SenderAddress, out _),
        $"{EmailOptions.Section}:{nameof(EmailOptions.SenderAddress)} isn't an email address.")
      .ValidateOnStart();

    // Fail at startup rather than when the first user can't confirm their account.
    builder.Services.AddOptions<AccountOptions>()
      .Validate<IOptions<EmailOptions>>(
        (accounts, email) => !(accounts.RequireConfirmedEmail && email.Value.DisableSending),
        $"{AccountOptions.Section}:RequireConfirmedEmail needs email: turn off {EmailOptions.Section}:DisableSending.")
      .ValidateOnStart();

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<IEmailTransport, EmailTransport>();
    builder.Services.AddSingleton<AccountEmails>();
    builder.Services.AddSingleton<IEmailSender<UserRecord>>(provider => provider.GetRequiredService<AccountEmails>());
    return builder;
  }
}
