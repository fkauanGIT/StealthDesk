using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>Creates users and signs them in, handing back the session cookie to send on later requests.</summary>
public static class TestAccounts
{
  public const string Password = "Correct-horse-9";

  public static async Task<UserRecord> CreateUserAsync(ServerHost server, string email, Guid? tenantId = null)
  {
    await using var scope = server.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<StealthDeskDb>();
    var users = scope.ServiceProvider.GetRequiredService<UserManager<UserRecord>>();

    var user = new UserRecord
    {
      UserName = email,
      Email = email,
      TenantId = tenantId ?? await db.Tenants.Select(x => x.Id).FirstAsync(),
    };
    var result = await users.CreateAsync(user, Password);
    Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(x => x.Description)));
    return user;
  }

  /// <summary>A client that keeps no cookies of its own, so tests choose exactly what each request carries.</summary>
  public static HttpClient Client(ServerHost server) =>
    server.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

  public static Task<HttpResponseMessage> SignInAsync(HttpClient client, string email, string password = Password) =>
    client.PostAsJsonAsync($"{Routes.Auth}/login?useCookies=true", new { email, password }, TestContext.Current.CancellationToken);

  /// <summary>Signs in and returns the cookie header value, e.g. ".AspNetCore.Identity.Application=...".</summary>
  public static async Task<string> SignInForCookieAsync(ServerHost server, string email)
  {
    using var client = Client(server);
    var response = await SignInAsync(client, email);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return SessionCookie(response) ?? throw new InvalidOperationException("The sign-in set no cookie.");
  }

  public static string? SessionCookie(HttpResponseMessage response) =>
    response.Headers.TryGetValues("Set-Cookie", out var values)
      ? values.Select(x => x.Split(';')[0]).FirstOrDefault(x => x.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal))
      : null;

  public static HttpRequestMessage WithCookie(HttpMethod method, string path, string cookie)
  {
    var request = new HttpRequestMessage(method, path);
    request.Headers.Add("Cookie", cookie);
    return request;
  }
}
