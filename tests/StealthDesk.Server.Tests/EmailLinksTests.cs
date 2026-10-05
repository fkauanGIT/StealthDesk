using StealthDesk.Web.Server.Email;

namespace StealthDesk.Server.Tests;

public class EmailLinksTests
{
  [Fact]
  public void Links_AreDecodedSoTheyOpenAsSent()
  {
    var html = "<p><a href=\"http://localhost/account/reset-password?email=ana%40example.com&amp;code=abc\">Reset</a></p>"
      + "<p>Or open this address: http://localhost/account/reset-password?email=ana%40example.com&amp;code=abc</p>";

    var links = EmailLinks.In(html);

    Assert.Equal(["http://localhost/account/reset-password?email=ana%40example.com&code=abc"], links);
  }

  [Fact]
  public void Message_WithoutLinks_HasNone() => Assert.Empty(EmailLinks.In("<p>Hello</p>"));
}
