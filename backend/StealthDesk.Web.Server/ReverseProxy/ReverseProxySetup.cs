using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace StealthDesk.Web.Server.ReverseProxy;

public static class ReverseProxySetup
{
  public static WebApplicationBuilder AddStealthDeskReverseProxy(this WebApplicationBuilder builder)
  {
    builder.Services.AddOptions<ReverseProxyOptions>()
      .Bind(builder.Configuration.GetSection(ReverseProxyOptions.Section))
      .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<ReverseProxyOptions>, ReverseProxyOptionsValidator>();
    builder.Services.AddHttpClient<CloudflareRanges>(http => http.Timeout = TimeSpan.FromSeconds(30));
    return builder;
  }

  /// <summary>
  /// Takes the client's address, scheme and host from the forwarded headers of a trusted proxy, before anything else
  /// reads them: cookies are <c>Secure</c> and redirect addresses <c>https://</c> behind a proxy that ends TLS, and
  /// logs and lockouts see the client instead of the proxy. A proxy in front of another is followed back as long as
  /// each one is trusted.
  /// </summary>
  public static async Task UseStealthDeskForwardedHeadersAsync(this WebApplication app)
  {
    var settings = app.Services.GetRequiredService<IOptions<ReverseProxyOptions>>().Value;
    var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.All, ForwardLimit = null };

    // With both lists empty the middleware checks no address at all.
    if (settings.TrustAllProxies)
    {
      options.KnownProxies.Clear();
      options.KnownIPNetworks.Clear();
      app.Logger.LogWarning("Forwarded headers are trusted from any address ({Setting} is on).", ReverseProxyOptions.Key(nameof(ReverseProxyOptions.TrustAllProxies)));
    }
    else
    {
      // The lists start with this machine's loopback addresses, for a proxy on the same machine. The middleware turns
      // a connection's IPv4-mapped address (::ffff:172.17.0.1) into plain IPv4 before comparing, so a setting written
      // either way is kept as plain IPv4.
      foreach (var proxy in settings.TrustedProxies.Append(settings.DockerGatewayAddress).Where(x => !string.IsNullOrEmpty(x)))
      {
        var address = IPAddress.Parse(proxy!);
        options.KnownProxies.Add(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
      }

      foreach (var network in settings.TrustedNetworks)
      {
        options.KnownIPNetworks.Add(IPNetwork.Parse(network));
      }

      if (settings.TrustCloudflare)
      {
        var ranges = await app.Services.GetRequiredService<CloudflareRanges>().FetchAsync(app.Lifetime.ApplicationStopping);
        foreach (var range in ranges)
        {
          options.KnownIPNetworks.Add(range);
        }

        app.Logger.LogInformation("Trusting {Count} Cloudflare address ranges.", ranges.Count);
      }
    }

    app.UseForwardedHeaders(options);
  }
}
