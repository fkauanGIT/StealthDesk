using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using StealthDesk.Web.Server.Email;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>An email transport that keeps the messages, so tests can read them and follow their links.</summary>
public sealed partial class CapturedEmails : IEmailTransport
{
  private readonly ConcurrentQueue<EmailMessage> _messages = new();

  public IReadOnlyList<EmailMessage> Messages => [.. _messages];

  public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
  {
    _messages.Enqueue(message);
    return Task.CompletedTask;
  }

  public EmailMessage To(string address) => Assert.Single(_messages, x => x.To == address);

  /// <summary>The link of the message's button, as a path and query on the server.</summary>
  public static string LinkIn(EmailMessage message)
  {
    var href = WebUtility.HtmlDecode(Link().Match(message.HtmlBody).Groups[1].Value);
    var uri = new Uri(href);
    return uri.PathAndQuery;
  }

  [GeneratedRegex("<a href=\"([^\"]+)\"")]
  private static partial Regex Link();
}
