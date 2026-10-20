using System.Net;
using IPNetwork = System.Net.IPNetwork;

namespace StealthDesk.Web.Server.ReverseProxy;

/// <summary>
/// Which proxies may tell the server who the client is (<c>X-Forwarded-For</c>) and how it connected
/// (<c>X-Forwarded-Proto</c>, <c>X-Forwarded-Host</c>). Bound from the <c>ReverseProxy</c> section. Only a proxy on
/// this machine is trusted unless one of these says otherwise.
/// </summary>
public sealed class ReverseProxyOptions
{
  public const string Section = "ReverseProxy";

  /// <summary>Addresses of trusted proxies, e.g. <c>10.0.0.2</c>.</summary>
  public string[] TrustedProxies { get; init; } = [];

  /// <summary>Networks of trusted proxies, e.g. <c>10.0.0.0/8</c>.</summary>
  public string[] TrustedNetworks { get; init; } = [];

  /// <summary>The gateway of the Docker network the server runs in, which a proxy in front of Docker comes through.</summary>
  public string? DockerGatewayAddress { get; init; }

  /// <summary>Trusts Cloudflare's address ranges, downloaded from Cloudflare when the server starts.</summary>
  public bool TrustCloudflare { get; init; }

  /// <summary>
  /// Trusts the forwarded headers of anyone who connects. Only for a server nothing but the proxy can reach: otherwise
  /// any client can claim any address.
  /// </summary>
  public bool TrustAllProxies { get; init; }

  public static string Key(string name) => $"{Section}:{name}";
}

/// <summary>Names the setting and the value a proxy setting can't use.</summary>
public sealed class ReverseProxyOptionsValidator : IValidateOptions<ReverseProxyOptions>
{
  public ValidateOptionsResult Validate(string? name, ReverseProxyOptions options)
  {
    var failures = new List<string>();
    foreach (var proxy in options.TrustedProxies.Where(x => !IPAddress.TryParse(x, out _)))
    {
      failures.Add($"{ReverseProxyOptions.Key(nameof(ReverseProxyOptions.TrustedProxies))} holds '{proxy}', which isn't an IP address, e.g. 10.0.0.2.");
    }

    foreach (var network in options.TrustedNetworks.Where(x => !IPNetwork.TryParse(x, out _)))
    {
      failures.Add($"{ReverseProxyOptions.Key(nameof(ReverseProxyOptions.TrustedNetworks))} holds '{network}', which isn't a network, e.g. 10.0.0.0/8.");
    }

    if (options.DockerGatewayAddress is { Length: > 0 } gateway && !IPAddress.TryParse(gateway, out _))
    {
      failures.Add($"{ReverseProxyOptions.Key(nameof(ReverseProxyOptions.DockerGatewayAddress))} is '{gateway}', which isn't an IP address, e.g. 172.17.0.1.");
    }

    return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
  }
}
