using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using StealthDesk.Branding;

namespace StealthDesk.Web.Server.Email;

/// <summary>
/// The account messages. Identity's endpoints call it to resend a confirmation, confirm an email change and reset a
/// password; registration calls it to confirm a new account. A singleton: Identity resolves it once at startup.
/// </summary>
public sealed class AccountEmails(IEmailTransport transport, IHttpContextAccessor http) : IEmailSender<UserRecord>
{
  public Task SendConfirmationLinkAsync(UserRecord user, string email, string confirmationLink) =>
    Send(email, $"Confirm your {Brand.Name} account",
      $"<p>Confirm your email address to finish setting up your {Brand.Name} account:</p>{Button(confirmationLink, "Confirm email")}");

  public Task SendPasswordResetLinkAsync(UserRecord user, string email, string resetLink) =>
    Send(email, $"Reset your {Brand.Name} password",
      $"<p>Someone asked to reset the password of this account. If it was you:</p>{Button(resetLink, "Choose a new password")}"
      + "<p>If it wasn't, you can ignore this message: the password stays as it is.</p>");

  // Identity's forgot-password endpoint hands over a code; the message turns it into a link to the reset page.
  public Task SendPasswordResetCodeAsync(UserRecord user, string email, string resetCode) =>
    SendPasswordResetLinkAsync(user, email, PageLink("account/reset-password", ("email", email), ("code", resetCode)));

  /// <summary>Sends the confirmation of a newly registered account, given the token Identity generated for it.</summary>
  public Task SendRegistrationConfirmationAsync(UserRecord user, string confirmationToken)
  {
    var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(confirmationToken));
    var link = PageLink(Routes.Auth.TrimStart('/') + "/confirmEmail", ("userId", user.Id.ToString()), ("code", code));
    return SendConfirmationLinkAsync(user, user.Email!, link);
  }

  private Task Send(string to, string subject, string body) =>
    transport.SendAsync(new EmailMessage(to, subject, Layout(body)));

  // HTML-encoded, like the links Identity's endpoints pass in, so every link reaches Button the same way.
  private string PageLink(string path, params (string Name, string Value)[] query)
  {
    var request = http.HttpContext?.Request
      ?? throw new InvalidOperationException("Account emails are only sent while handling a request.");
    var url = $"{request.Scheme}://{request.Host}{request.PathBase}/{path}";
    return HtmlEncoder.Default.Encode(QueryHelpers.AddQueryString(url, query.ToDictionary(x => x.Name, x => (string?)x.Value)));
  }

  /// <param name="href">Already HTML-encoded.</param>
  private static string Button(string href, string text)
  {
    return $"<p><a href=\"{href}\" style=\"display:inline-block;padding:12px 20px;border-radius:8px;"
      + $"background:#6C47FF;color:#FFFFFF;text-decoration:none;font-weight:600\">{text}</a></p>"
      + $"<p style=\"color:#6A6675;font-size:13px\">Or open this address: {href}</p>";
  }

  private static string Layout(string body) =>
    "<div style=\"font-family:'Segoe UI',Arial,sans-serif;font-size:15px;color:#16141C;max-width:520px\">"
    + $"<p style=\"font-size:18px;font-weight:700\">{Brand.Name}</p>{body}</div>";
}
