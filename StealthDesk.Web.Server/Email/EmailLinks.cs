using System.Net;
using System.Text.RegularExpressions;

namespace StealthDesk.Web.Server.Email;

public static partial class EmailLinks
{
  /// <summary>The addresses a message's links open, decoded from the HTML, each once.</summary>
  public static IReadOnlyList<string> In(string html) =>
    [.. Href().Matches(html).Select(x => WebUtility.HtmlDecode(x.Groups[1].Value)).Distinct()];

  [GeneratedRegex("<a href=\"([^\"]+)\"")]
  private static partial Regex Href();
}
