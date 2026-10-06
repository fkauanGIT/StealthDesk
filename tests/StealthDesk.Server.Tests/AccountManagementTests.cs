using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>A signed-in user managing their own account: profile, password, email, personal data and deletion.</summary>
public class AccountManagementTests
{
  private const string NewPassword = "Brand-new-Passw0rd";

  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task Profile_ShowsTheSignedInAccount()
  {
    using var server = ServerHost.InMemory();
    var user = await TestAccounts.CreateUserAsync(server, "ana@example.com", confirmed: true);
    using var client = await SignedInAsync(server, "ana@example.com");

    var profile = await client.GetFromJsonAsync<AccountProfile>(Routes.AccountProfile, Cancel);

    Assert.Equal(user.Id, profile!.Id);
    Assert.Equal("ana@example.com", profile.Email);
    Assert.True(profile.EmailConfirmed);
    Assert.True(profile.HasPassword);
  }

  [Fact]
  public async Task Profile_SavesThePhoneNumberAndKeepsTheSession()
  {
    using var server = ServerHost.InMemory().WithServices(CheckSessionsOnEveryRequest);
    await TestAccounts.CreateUserAsync(server, "bia@example.com");
    var cookie = await TestAccounts.SignInForCookieAsync(server, "bia@example.com");
    using var client = TestAccounts.Client(server);

    var invalid = await SendAsync(client, HttpMethod.Put, Routes.AccountProfile, cookie, new ProfileUpdate { PhoneNumber = "call me" });
    var saved = await SendAsync(client, HttpMethod.Put, Routes.AccountProfile, cookie, new ProfileUpdate { PhoneNumber = "+55 11 98765-4321" });
    var renewed = TestAccounts.SessionCookie(saved);

    Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    Assert.Contains("InvalidPhoneNumber", await invalid.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    Assert.Equal("+55 11 98765-4321", (await saved.Content.ReadFromJsonAsync<AccountProfile>(Cancel))!.PhoneNumber);
    Assert.NotNull(renewed);
    Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, Routes.CurrentUser, renewed!)).StatusCode);
  }

  [Fact]
  public async Task ChangePassword_KeepsThisSessionAndSignsTheOthersOut()
  {
    using var server = ServerHost.InMemory().WithServices(CheckSessionsOnEveryRequest);
    await TestAccounts.CreateUserAsync(server, "caio@example.com");
    var here = await TestAccounts.SignInForCookieAsync(server, "caio@example.com");
    var elsewhere = await TestAccounts.SignInForCookieAsync(server, "caio@example.com");
    using var client = TestAccounts.Client(server);

    var change = await SendAsync(client, HttpMethod.Post, Routes.AccountPassword, here,
      new PasswordChange { CurrentPassword = TestAccounts.Password, NewPassword = NewPassword });
    var renewed = TestAccounts.SessionCookie(change);

    Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, Routes.CurrentUser, renewed!)).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, HttpMethod.Get, Routes.CurrentUser, elsewhere)).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await TestAccounts.SignInAsync(client, "caio@example.com")).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await TestAccounts.SignInAsync(client, "caio@example.com", NewPassword)).StatusCode);
  }

  [Fact]
  public async Task ChangePassword_WithTheWrongCurrentPassword_IsRefused()
  {
    using var server = ServerHost.InMemory();
    await TestAccounts.CreateUserAsync(server, "duda@example.com");
    using var client = await SignedInAsync(server, "duda@example.com");

    var change = await client.PostAsJsonAsync(Routes.AccountPassword,
      new PasswordChange { CurrentPassword = "Wrong-password-1", NewPassword = NewPassword }, Cancel);
    var weak = await client.PostAsJsonAsync(Routes.AccountPassword,
      new PasswordChange { CurrentPassword = TestAccounts.Password, NewPassword = "short" }, Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
    Assert.Contains("PasswordMismatch", await change.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
    Assert.Contains("PasswordTooShort", await weak.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(HttpStatusCode.OK, (await TestAccounts.SignInAsync(client, "duda@example.com")).StatusCode);
  }

  [Fact]
  public async Task SetPassword_OnlyForAccountsWithoutOne()
  {
    using var server = ServerHost.InMemory();
    var user = await TestAccounts.CreateUserAsync(server, "edu@example.com");
    using var client = await SignedInAsync(server, "edu@example.com");

    var refused = await client.PostAsJsonAsync(Routes.AccountPasswordSet, new PasswordSet { NewPassword = NewPassword }, Cancel);
    await RemovePasswordAsync(server, user.Id);
    var profile = await client.GetFromJsonAsync<AccountProfile>(Routes.AccountProfile, Cancel);
    var set = await client.PostAsJsonAsync(Routes.AccountPasswordSet, new PasswordSet { NewPassword = NewPassword }, Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    Assert.False(profile!.HasPassword);
    Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await TestAccounts.SignInAsync(client, "edu@example.com", NewPassword)).StatusCode);
  }

  [Fact]
  public async Task MustChangePassword_KeepsTheUserOutOfTheApiUntilTheyChangeIt()
  {
    using var server = ServerHost.InMemory();
    var user = await TestAccounts.CreateUserAsync(server, "fabi@example.com");
    await MustChangePasswordAsync(server, user.Id);
    using var client = await SignedInAsync(server, "fabi@example.com");

    var devices = await client.GetAsync(Routes.Devices, Cancel);
    var hub = await client.PostAsync($"{Routes.Dashboard}/negotiate?negotiateVersion=1", null, Cancel);
    var me = await client.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel);
    var change = await client.PostAsJsonAsync(Routes.AccountPassword,
      new PasswordChange { CurrentPassword = TestAccounts.Password, NewPassword = NewPassword }, Cancel);

    Assert.Equal(HttpStatusCode.Forbidden, devices.StatusCode);
    Assert.Contains("Password change required.", await devices.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(HttpStatusCode.Forbidden, hub.StatusCode);
    Assert.True(me!.MustChangePassword);
    Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Routes.Devices, Cancel)).StatusCode);
    Assert.False(await server.WithDbAsync(db => db.Users.Select(x => x.MustChangePassword).SingleAsync()));
  }

  [Fact]
  public async Task PasswordReset_EndsAForcedPasswordChange()
  {
    var emails = new CapturedEmails();
    using var server = ServerHost.InMemory().WithCapturedEmails(emails);
    var user = await TestAccounts.CreateUserAsync(server, "gil@example.com", confirmed: true);
    await MustChangePasswordAsync(server, user.Id);
    using var client = TestAccounts.Client(server);

    await client.PostAsJsonAsync($"{Routes.Auth}/forgotPassword", new { email = "gil@example.com" }, Cancel);
    var link = new Uri(new Uri("http://x"), CapturedEmails.LinkIn(emails.To("gil@example.com")));
    var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(link.Query);
    var reset = await client.PostAsJsonAsync($"{Routes.Auth}/resetPassword",
      new { email = "gil@example.com", resetCode = query["code"].ToString(), newPassword = NewPassword }, Cancel);

    Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.Users.Select(x => x.MustChangePassword).SingleAsync()));
  }

  [Fact]
  public async Task EmailChange_ConfirmsOnTheAccountPageAndKeepsTheSession()
  {
    var emails = new CapturedEmails();
    using var server = ServerHost.InMemory().WithCapturedEmails(emails).WithServices(CheckSessionsOnEveryRequest);
    await TestAccounts.CreateUserAsync(server, "hugo@example.com");
    var cookie = await TestAccounts.SignInForCookieAsync(server, "hugo@example.com");
    using var client = server.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    await SendAsync(client, HttpMethod.Post, $"{Routes.Auth}/manage/info", cookie, new { newEmail = "hugo.new@example.com" });
    var message = emails.To("hugo.new@example.com");
    var confirm = await SendAsync(client, HttpMethod.Get, CapturedEmails.LinkIn(message), cookie);
    var renewed = TestAccounts.SessionCookie(confirm);
    var me = await SendAsync(client, HttpMethod.Get, Routes.CurrentUser, renewed!);

    Assert.Equal("Confirm your new StealthDesk email address", message.Subject);
    Assert.Equal("/account/email-confirmed?changed=true", confirm.Headers.Location!.OriginalString);
    Assert.Equal("hugo.new@example.com", (await me.Content.ReadFromJsonAsync<CurrentUser>(Cancel))!.Email);
  }

  [Fact]
  public async Task EmailChange_LinkOpenedSignedOut_ChangesTheEmailWithoutErrors()
  {
    var emails = new CapturedEmails();
    var logs = new CapturedLogs();
    using var server = ServerHost.InMemory().WithCapturedEmails(emails)
      .WithServices(services => services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs));
    await TestAccounts.CreateUserAsync(server, "ivo@example.com");
    var cookie = await TestAccounts.SignInForCookieAsync(server, "ivo@example.com");
    using var client = server.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
    await SendAsync(client, HttpMethod.Post, $"{Routes.Auth}/manage/info", cookie, new { newEmail = "ivo.new@example.com" });

    // Another browser: no session cookie.
    var confirm = await client.GetAsync(CapturedEmails.LinkIn(emails.To("ivo.new@example.com")), Cancel);

    Assert.Equal("/account/email-confirmed?changed=true", confirm.Headers.Location!.OriginalString);
    Assert.Null(TestAccounts.SessionCookie(confirm));
    Assert.Equal("ivo.new@example.com", await server.WithDbAsync(db => db.Users.Select(x => x.Email).SingleAsync()));
    Assert.Empty(logs.Errors);
  }

  [Fact]
  public async Task PersonalData_DownloadsTheUsersOwnData()
  {
    using var server = ServerHost.InMemory();
    var user = await TestAccounts.CreateUserAsync(server, "iris@example.com");
    using var client = await SignedInAsync(server, "iris@example.com");

    var download = await client.GetAsync(Routes.AccountPersonalData, Cancel);
    var data = await download.Content.ReadFromJsonAsync<JsonElement>(Cancel);

    Assert.Equal(HttpStatusCode.OK, download.StatusCode);
    Assert.Equal("PersonalData.json", download.Content.Headers.ContentDisposition!.FileName);
    Assert.Equal(user.Id.ToString(), data.GetProperty("Id").GetString());
    Assert.Equal("iris@example.com", data.GetProperty("Email").GetString());
    Assert.Equal(user.CreatedAt, data.GetProperty("CreatedAt").GetDateTimeOffset());
    Assert.False(data.GetProperty("TwoFactorEnabled").GetBoolean());
    Assert.DoesNotContain("\\u002B", await download.Content.ReadAsStringAsync(Cancel));
    Assert.False(data.TryGetProperty("PasswordHash", out _));
    Assert.False(data.TryGetProperty("SecurityStamp", out _));
  }

  [Fact]
  public async Task Delete_RequiresThePasswordAndRemovesTheUser()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await TestAccounts.CreateUserAsync(server, "joao@example.com");
    await TestAccounts.CreateUserAsync(server, "kaue@example.com");
    using var client = await SignedInAsync(server, "joao@example.com");

    var wrong = await client.PostAsJsonAsync(Routes.AccountDeletion, new AccountDeletion { Password = "Wrong-password-1" }, Cancel);
    var stillThere = await server.WithDbAsync(db => db.Users.CountAsync());
    var deleted = await client.PostAsJsonAsync(Routes.AccountDeletion, new AccountDeletion { Password = TestAccounts.Password }, Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
    Assert.Contains("PasswordMismatch", await wrong.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(2, stillThere);
    Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    Assert.Contains(deleted.Headers.GetValues("Set-Cookie"), x => x.StartsWith(".AspNetCore.Identity.Application=;", StringComparison.Ordinal));
    Assert.Equal(["kaue@example.com"], await server.WithDbAsync(db => db.Users.Select(x => x.Email!).ToListAsync()));
    Assert.Equal(HttpStatusCode.Unauthorized, (await TestAccounts.SignInAsync(client, "joao@example.com")).StatusCode);
  }

  [Fact]
  public async Task AccountEndpoints_RequireSignIn()
  {
    using var server = ServerHost.InMemory();
    using var client = TestAccounts.Client(server);

    Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Routes.AccountProfile, Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Routes.AccountPersonalData, Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized,
      (await client.PostAsJsonAsync(Routes.AccountDeletion, new AccountDeletion { Password = TestAccounts.Password }, Cancel)).StatusCode);
  }

  // Identity checks a session against the user's security stamp every 30 minutes; tests can't wait that long.
  private static void CheckSessionsOnEveryRequest(IServiceCollection services) =>
    services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);

  private static async Task<HttpClient> SignedInAsync(ServerHost server, string email)
  {
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
    return client;
  }

  private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string cookie, object? body = null)
  {
    var request = TestAccounts.WithCookie(method, path, cookie);
    if (body is not null)
    {
      request.Content = JsonContent.Create(body);
    }

    return client.SendAsync(request, Cancel);
  }

  private static async Task MustChangePasswordAsync(ServerHost server, Guid userId)
  {
    await using var scope = server.Services.CreateAsyncScope();
    var users = scope.ServiceProvider.GetRequiredService<UserManager<UserRecord>>();
    var user = await users.FindByIdAsync(userId.ToString());
    user!.MustChangePassword = true;
    await users.UpdateAsync(user);
  }

  // As if the account had only an external login. The session stays: the stamp isn't checked again for 30 minutes.
  private static async Task RemovePasswordAsync(ServerHost server, Guid userId)
  {
    await using var scope = server.Services.CreateAsyncScope();
    var users = scope.ServiceProvider.GetRequiredService<UserManager<UserRecord>>();
    var result = await users.RemovePasswordAsync((await users.FindByIdAsync(userId.ToString()))!);
    Assert.True(result.Succeeded);
  }
}
