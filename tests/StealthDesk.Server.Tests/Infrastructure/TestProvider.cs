using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StealthDesk.Web.Server.Accounts;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>
/// A sign-in provider like Microsoft or GitHub, without leaving the server: its challenge sends the browser straight
/// back to its callback, as if the user had signed in there as <see cref="Account"/>. Identity's own code does the rest.
/// </summary>
public sealed class TestProvider
{
  public const string Scheme = "TestProvider";
  public const string DisplayName = "Test provider";

  /// <summary>Who the provider says is signing in; null makes the user turn the provider down.</summary>
  public (string Id, string? Email)? Account { get; set; } = ("provider-id-1", "ana@provider.example");

  /// <summary>Adds the provider to a server, with the same handling of refusals and failures as the real ones.</summary>
  public ServerHost AddTo(ServerHost server) => server.WithServices(services =>
  {
    services.AddSingleton(this);
    services.AddAuthentication().AddRemoteScheme<Options, Handler>(Scheme, DisplayName, options =>
    {
      options.CallbackPath = "/signin-test";
      AccountSetup.BackToSignIn(options);
    });
  });

  /// <summary>A client that keeps cookies, like a browser, but lets the test follow each redirect.</summary>
  public static HttpClient Browser(ServerHost server) =>
    server.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

  /// <summary>Follows the server's redirects until they reach a page of the web client, and returns its address.</summary>
  public static async Task<string> FollowAsync(HttpClient browser, string path)
  {
    for (var hops = 0; hops < 10; hops++)
    {
      var response = await browser.GetAsync(path, TestContext.Current.CancellationToken);
      if (response.StatusCode != HttpStatusCode.Redirect)
      {
        return $"{(int)response.StatusCode} at {path}";
      }

      path = response.Headers.Location!.OriginalString;
      if (!path.StartsWith("/api/", StringComparison.Ordinal) && !path.StartsWith("/signin-", StringComparison.Ordinal))
      {
        return path;
      }
    }

    throw new InvalidOperationException("Too many redirects.");
  }

  public sealed class Options : RemoteAuthenticationOptions
  {
    // Real providers start with their events object; the base options leave it null.
    public Options() => Events = new RemoteAuthenticationEvents();
  }

  private sealed class Handler(
    IOptionsMonitor<Options> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IDataProtectionProvider dataProtection,
    TestProvider provider)
    : RemoteAuthenticationHandler<Options>(options, logger, encoder)
  {
    // Like a real provider's state: the properties travel protected, so the callback can trust them.
    private readonly PropertiesDataFormat _state = new(dataProtection.CreateProtector(nameof(TestProvider)));

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
      // The provider's page is skipped: back to the callback at once, carrying the protected properties as state.
      var state = _state.Protect(properties);
      Response.Redirect($"{Options.CallbackPath}?state={Uri.EscapeDataString(state)}");
      return Task.CompletedTask;
    }

    protected override async Task<HandleRequestResult> HandleRemoteAuthenticateAsync()
    {
      var properties = _state.Unprotect(Request.Query["state"]);
      if (properties is null)
      {
        return HandleRequestResult.Fail("The state is missing or invalid.");
      }

      if (provider.Account is not { } account)
      {
        return await HandleAccessDeniedErrorAsync(properties);
      }

      var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, account.Id), new(ClaimTypes.Name, account.Id) };
      if (account.Email is not null)
      {
        claims.Add(new Claim(ClaimTypes.Email, account.Email));
      }

      var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
      return HandleRequestResult.Success(new AuthenticationTicket(principal, properties, Scheme.Name));
    }
  }
}
