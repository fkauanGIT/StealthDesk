using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>Confirming the address, resetting the password and changing the email, through the messages they send.</summary>
public class AccountEmailTests
{
  private const string NewPassword = "Brand-new-Passw0rd";

  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task Registration_SendsAConfirmationLinkThatConfirmsTheAccount()
  {
    var emails = new CapturedEmails();
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true").WithCapturedEmails(emails);
    using var client = TestAccounts.Client(server);
    await RegisterAsync(client, "first@example.com");

    await RegisterAsync(client, "second@example.com");
    var confirm = await client.GetAsync(CapturedEmails.LinkIn(emails.To("second@example.com")), Cancel);

    Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
    Assert.True(await server.WithDbAsync(db => db.Users.Where(x => x.Email == "second@example.com").Select(x => x.EmailConfirmed).SingleAsync()));
    Assert.DoesNotContain(emails.Messages, x => x.To == "first@example.com");
  }

  [Fact]
  public async Task FirstUser_IsConfirmedWithoutAnEmail()
  {
    var emails = new CapturedEmails();
    using var server = ServerHost.InMemory().WithCapturedEmails(emails);
    using var client = TestAccounts.Client(server);

    await RegisterAsync(client, "first@example.com");

    Assert.Empty(emails.Messages);
    Assert.True(await server.WithDbAsync(db => db.Users.Select(x => x.EmailConfirmed).SingleAsync()));
  }

  [Fact]
  public async Task WithSendingDisabled_RegistrationConfirmsRightAway()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    using var client = TestAccounts.Client(server);
    await RegisterAsync(client, "first@example.com");

    await RegisterAsync(client, "second@example.com");

    Assert.True(await server.WithDbAsync(db => db.Users.Where(x => x.Email == "second@example.com").Select(x => x.EmailConfirmed).SingleAsync()));
  }

  [Fact]
  public async Task ResendConfirmation_SendsAnotherLink()
  {
    var emails = new CapturedEmails();
    using var server = ServerHost.InMemory().WithCapturedEmails(emails);
    await TestAccounts.CreateUserAsync(server, "ana@example.com");
    using var client = TestAccounts.Client(server);

    var resend = await client.PostAsJsonAsync($"{Routes.Auth}/resendConfirmationEmail", new { email = "ana@example.com" }, Cancel);
    var confirm = await client.GetAsync(CapturedEmails.LinkIn(emails.To("ana@example.com")), Cancel);

    Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
    Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
    Assert.True(await server.WithDbAsync(db => db.Users.Select(x => x.EmailConfirmed).SingleAsync()));
  }

  [Fact]
  public async Task PasswordReset_LinkSetsANewPasswordOnce()
  {
    var emails = new CapturedEmails();
    using var server = ServerHost.InMemory().WithCapturedEmails(emails);
    await TestAccounts.CreateUserAsync(server, "bruno@example.com", confirmed: true);
    using var client = TestAccounts.Client(server);

    await client.PostAsJsonAsync($"{Routes.Auth}/forgotPassword", new { email = "bruno@example.com" }, Cancel);
    var link = CapturedEmails.LinkIn(emails.To("bruno@example.com"));
    var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(new Uri("http://x"), link).Query);
    var reset = new { email = query["email"].ToString(), resetCode = query["code"].ToString(), newPassword = NewPassword };

    var first = await client.PostAsJsonAsync($"{Routes.Auth}/resetPassword", reset, Cancel);
    var again = await client.PostAsJsonAsync($"{Routes.Auth}/resetPassword", reset with { newPassword = "Another-Passw0rd" }, Cancel);
    var signIn = await TestAccounts.SignInAsync(client, "bruno@example.com", NewPassword);

    Assert.StartsWith("/account/reset-password?", link, StringComparison.Ordinal);
    Assert.Equal(HttpStatusCode.OK, first.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
  }

  [Fact]
  public async Task EmailChange_IsAppliedOnlyOnceTheNewAddressConfirms()
  {
    var emails = new CapturedEmails();
    using var server = ServerHost.InMemory().WithCapturedEmails(emails);
    await TestAccounts.CreateUserAsync(server, "carla@example.com");
    var cookie = await TestAccounts.SignInForCookieAsync(server, "carla@example.com");
    using var client = TestAccounts.Client(server);

    var change = TestAccounts.WithCookie(HttpMethod.Post, $"{Routes.Auth}/manage/info", cookie);
    change.Content = JsonContent.Create(new { newEmail = "carla.new@example.com" });
    await client.SendAsync(change, Cancel);
    var before = await server.WithDbAsync(db => db.Users.Select(x => x.Email).SingleAsync());
    await client.GetAsync(CapturedEmails.LinkIn(emails.To("carla.new@example.com")), Cancel);
    var after = await server.WithDbAsync(db => db.Users.Select(x => x.Email).SingleAsync());

    Assert.Equal("carla@example.com", before);
    Assert.Equal("carla.new@example.com", after);
  }

  [Fact]
  public async Task RequiredConfirmation_KeepsUnconfirmedUsersOut()
  {
    var emails = new CapturedEmails();
    using var server = ServerHost.InMemory().With("Accounts:RequireConfirmedEmail", "true").WithCapturedEmails(emails);
    await TestAccounts.CreateUserAsync(server, "dani@example.com");
    using var client = TestAccounts.Client(server);

    var before = await TestAccounts.SignInAsync(client, "dani@example.com");
    await client.PostAsJsonAsync($"{Routes.Auth}/resendConfirmationEmail", new { email = "dani@example.com" }, Cancel);
    await client.GetAsync(CapturedEmails.LinkIn(emails.To("dani@example.com")), Cancel);
    var after = await TestAccounts.SignInAsync(client, "dani@example.com");

    Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);
    Assert.Contains("NotAllowed", await before.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(HttpStatusCode.OK, after.StatusCode);
  }

  [Fact]
  public void RequiringConfirmationWithoutSending_StopsTheServerAtStartup()
  {
    using var server = ServerHost.InMemory()
      .With("Accounts:RequireConfirmedEmail", "true")
      .With("Email:DisableSending", "true");

    var error = Assert.ThrowsAny<Exception>(() => server.CreateClient());

    var validation = error as OptionsValidationException ?? error.InnerException as OptionsValidationException;
    Assert.NotNull(validation);
    Assert.Contains("RequireConfirmedEmail", validation.Message);
  }

  [Theory]
  [InlineData(true, false)]
  [InlineData(false, true)]
  public async Task DisabledSending_NeverConnectsToTheSmtpServer(bool disabled, bool expectConnection)
  {
    using var smtp = new TcpListener(IPAddress.Loopback, 0);
    smtp.Start();
    var port = ((IPEndPoint)smtp.LocalEndpoint).Port;
    using var server = ServerHost.InMemory()
      .With("Email:DisableSending", disabled.ToString())
      .With("Email:SmtpHost", "127.0.0.1")
      .With("Email:SmtpPort", port.ToString())
      .With("Email:SenderAddress", "stealthdesk@example.com");
    await TestAccounts.CreateUserAsync(server, "edu@example.com", confirmed: true);
    using var client = TestAccounts.Client(server);

    // The fake server never answers, so a real attempt connects and then gives up; the request still succeeds.
    var accept = smtp.AcceptTcpClientAsync(Cancel).AsTask();
    var forgot = client.PostAsJsonAsync($"{Routes.Auth}/forgotPassword", new { email = "edu@example.com" }, Cancel);
    var connected = await Task.WhenAny(accept, Task.Delay(TimeSpan.FromSeconds(3), Cancel)) == accept;
    if (connected)
    {
      (await accept).Dispose();
    }

    Assert.Equal(expectConnection, connected);
    Assert.Equal(HttpStatusCode.OK, (await forgot).StatusCode);
  }

  [Fact]
  public async Task RealSmtp_DeliversTheMessage()
  {
    var (host, port) = await Mailpit.SmtpAsync();
    using var server = ServerHost.InMemory()
      .With("Email:DisableSending", "false")
      .With("Email:SmtpHost", host)
      .With("Email:SmtpPort", port.ToString())
      .With("Email:SenderAddress", "stealthdesk@example.com");
    var address = $"{Guid.NewGuid():N}@example.com";
    await TestAccounts.CreateUserAsync(server, address, confirmed: true);
    using var client = TestAccounts.Client(server);

    await client.PostAsJsonAsync($"{Routes.Auth}/forgotPassword", new { email = address }, Cancel);

    using var mailpit = new HttpClient { BaseAddress = await Mailpit.ApiAsync() };
    var search = await mailpit.GetFromJsonAsync<JsonElement>($"api/v1/search?query=to:{address}", Cancel);
    var message = Assert.Single(search.GetProperty("messages").EnumerateArray());
    Assert.Equal("Reset your StealthDesk password", message.GetProperty("Subject").GetString());
  }

  private static async Task RegisterAsync(HttpClient client, string email)
  {
    var response = await client.PostAsJsonAsync($"{Routes.Auth}/register", new { email, password = TestAccounts.Password }, Cancel);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
  }
}
