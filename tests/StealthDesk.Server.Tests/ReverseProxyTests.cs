using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.PersonalAccessTokens;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.ReverseProxy;

namespace StealthDesk.Server.Tests;

public sealed class ReverseProxyTests
{
  private const string Proxy = "10.0.0.2";
  private const string Stranger = "198.51.100.7";
  private const string ClientAddress = "203.0.113.9";
  private const string CloudflareEdge = "2400:cb00::7";

  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  // The OAuth redirect address is built from the request's scheme and host, so it shows which ones the server saw.
  [Theory]
  [InlineData(null, null, "127.0.0.1", true)]
  [InlineData(null, null, "::1", true)]
  [InlineData(null, null, Proxy, false)]
  [InlineData("ReverseProxy:TrustedProxies:0", Proxy, Proxy, true)]
  [InlineData("ReverseProxy:TrustedProxies:0", Proxy, Stranger, false)]
  [InlineData("ReverseProxy:TrustedProxies:0", "::ffff:10.0.0.2", Proxy, true)]
  [InlineData("ReverseProxy:TrustedNetworks:0", "10.0.0.0/8", "10.1.2.3", true)]
  [InlineData("ReverseProxy:TrustedNetworks:0", "10.0.0.0/8", Stranger, false)]
  [InlineData("ReverseProxy:DockerGatewayAddress", "172.17.0.1", "::ffff:172.17.0.1", true)]
  [InlineData("ReverseProxy:DockerGatewayAddress", "::ffff:172.17.0.1", "172.17.0.1", true)]
  [InlineData("ReverseProxy:TrustAllProxies", "true", Stranger, true)]
  public async Task ForwardedHeaders_AreUsed_OnlyFromATrustedProxy(string? key, string? value, string from, bool trusted)
  {
    using var server = Server(key, value);

    var redirect = await RedirectBackAsync(server, EndingTls(from));

    Assert.Equal(trusted ? "https://desk.example.com/signin-github" : "http://localhost/signin-github", redirect);
  }

  [Theory]
  [InlineData(Proxy, true)]
  [InlineData(Stranger, false)]
  public async Task SigningInThroughAProxyThatEndsTls_SetsASecureCookie_OnlyWhenTheProxyIsTrusted(string from, bool secure)
  {
    using var server = BehindProxy();
    var email = $"{Guid.NewGuid():N}@example.com";
    await TestAccounts.CreateUserAsync(server, email);
    using var client = EndingTls(from).Client(server);

    using var response = await TestAccounts.SignInAsync(client, email);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), x => x.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal));
    Assert.Equal(secure, cookie.Contains("; secure", StringComparison.OrdinalIgnoreCase));
  }

  [Theory]
  [InlineData(Proxy, ClientAddress)]
  [InlineData(Stranger, Stranger)]
  public async Task AChangeThroughAProxy_RecordsTheClientsAddress_OnlyWhenTheProxyIsTrusted(string from, string recorded)
  {
    using var server = BehindProxy();

    Assert.Equal(recorded, await AddressRecordedAsync(server, new TestProxy(from) { ForwardedFor = ClientAddress }));
  }

  // The agent's realtime connection goes through the proxy like any request, and its address is what the device shows.
  [Theory]
  [InlineData(Proxy, ClientAddress)]
  [InlineData(Stranger, Stranger)]
  public async Task AnAgentThroughAProxy_Reports_FromTheClientsAddress_OnlyWhenTheProxyIsTrusted(string from, string seen)
  {
    using var server = BehindProxy();
    await using var agent = await TestAgent.ConnectAsync(server, through: new TestProxy(from) { ForwardedFor = ClientAddress });
    var deviceId = Guid.NewGuid();

    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);

    Assert.Equal(seen, await server.WithDbAsync(db => db.Devices.IgnoreQueryFilters().Where(x => x.Id == deviceId).Select(x => x.PublicIpV4).SingleAsync(Cancel)));
  }

  // The client reaches Cloudflare over IPv6, Cloudflare reaches the proxy, and the proxy the server: each trusted hop
  // is followed back to the one before it.
  [Theory]
  [InlineData(true, ClientAddress)]
  [InlineData(false, CloudflareEdge)]
  public async Task BehindCloudflare_TheClientIsFollowedBackThroughBothProxies_WhenCloudflareIsTrusted(bool trustCloudflare, string recorded)
  {
    using var server = WithCloudflare(BehindProxy(), "104.16.0.0/13\n172.64.0.0/13\n", "2400:cb00::/32\n2606:4700::/32\n")
      .With("ReverseProxy:TrustCloudflare", trustCloudflare.ToString());

    var address = await AddressRecordedAsync(server, new TestProxy(Proxy) { ForwardedFor = $"{ClientAddress}, {CloudflareEdge}" });

    Assert.Equal(recorded, address);
  }

  [Theory]
  [InlineData(null, "couldn't be downloaded from https://www.cloudflare.com/ips-v4")]
  [InlineData("104.16.0.0/13\nnot a range\n", "answered 'not a range'")]
  public void WhenCloudflaresRangesCantBeRead_TheServerDoesntStart(string? ipV4, string reason)
  {
    using var server = WithCloudflare(ServerHost.InMemory(), ipV4, "2400:cb00::/32\n").With("ReverseProxy:TrustCloudflare", "true");

    var refusal = server.RefusalToStart();

    Assert.Contains("ReverseProxy:TrustCloudflare is on", refusal, StringComparison.Ordinal);
    Assert.Contains(reason, refusal, StringComparison.Ordinal);
  }

  private static ServerHost Server(string? key = null, string? value = null)
  {
    var server = TestProxy.AddTo(ServerHost.InMemory())
      .With("Accounts:GitHubClientId", "github-client")
      .With("Accounts:GitHubClientSecret", "secret");
    return key is null ? server : server.With(key, value);
  }

  private static ServerHost BehindProxy() => Server("ReverseProxy:TrustedProxies:0", Proxy);

  /// <summary>A proxy that took the client's HTTPS connection to <c>desk.example.com</c> and passes it on as HTTP.</summary>
  private static TestProxy EndingTls(string from) => new(from)
  {
    ForwardedFor = ClientAddress,
    ForwardedProto = "https",
    ForwardedHost = "desk.example.com",
  };

  /// <summary>Where GitHub is told to send the user back after signing in.</summary>
  private static async Task<string> RedirectBackAsync(ServerHost server, TestProxy proxy)
  {
    using var client = proxy.Client(server);
    using var challenge = await client.GetAsync(Routes.ExternalSignIn("GitHub"), Cancel);

    Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
    return QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["redirect_uri"].ToString();
  }

  /// <summary>The address the authorization log records for a token a new user creates through the proxy.</summary>
  private static async Task<string?> AddressRecordedAsync(ServerHost server, TestProxy proxy)
  {
    var email = $"{Guid.NewGuid():N}@example.com";
    var user = await TestAccounts.CreateUserAsync(server, email);
    await TestPermissions.AssignAsync(server, user, PermissionNames.PersonalAccessTokenSelfWrite, PermissionScopeKind.Tenant, user.TenantId);
    var cookie = await TestAccounts.SignInForCookieAsync(server, email);
    using var client = proxy.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", cookie);

    using var created = await client.PostAsJsonAsync(Routes.PersonalAccessTokens, new CreatePersonalAccessTokenRequest("Behind the proxy", PersonalAccessTokenMode.InheritOwner), Cancel);

    created.EnsureSuccessStatusCode();
    return await server.WithDbAsync(db => db.AuthorizationChanges
      .Where(x => x.Action == AuthorizationChangeActions.PersonalAccessTokenCreated)
      .Select(x => x.IpAddress)
      .SingleAsync(Cancel));
  }

  /// <summary>Cloudflare's lists as given; a null list fails to download.</summary>
  private static ServerHost WithCloudflare(ServerHost server, string? ipV4, string? ipV6) =>
    server.WithServices(services => services
      .AddHttpClient<CloudflareRanges>()
      .ConfigurePrimaryHttpMessageHandler(() => new Lists(new() { [CloudflareRanges.IpV4] = ipV4, [CloudflareRanges.IpV6] = ipV6 })));

  private sealed class Lists(Dictionary<Uri, string?> lists) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
      Task.FromResult(lists.GetValueOrDefault(request.RequestUri!) is { } text
        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) }
        : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
  }
}
