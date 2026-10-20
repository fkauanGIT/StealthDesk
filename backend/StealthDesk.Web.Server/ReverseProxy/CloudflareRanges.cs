using IPNetwork = System.Net.IPNetwork;

namespace StealthDesk.Web.Server.ReverseProxy;

/// <summary>The address ranges Cloudflare's proxies come from, as Cloudflare publishes them.</summary>
public sealed class CloudflareRanges(HttpClient http)
{
  public static readonly Uri IpV4 = new("https://www.cloudflare.com/ips-v4");
  public static readonly Uri IpV6 = new("https://www.cloudflare.com/ips-v6");

  /// <summary>
  /// Both lists, or an error naming the setting: a server told to trust Cloudflare that trusted nothing would record
  /// Cloudflare's address for every client, and keep cookies off <c>Secure</c>, without anyone noticing.
  /// </summary>
  public async Task<IReadOnlyList<IPNetwork>> FetchAsync(CancellationToken cancellationToken)
  {
    var ranges = new List<IPNetwork>();
    foreach (var list in new[] { IpV4, IpV6 })
    {
      string text;
      try
      {
        text = await http.GetStringAsync(list, cancellationToken);
      }
      catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
      {
        throw Refusal($"its address ranges couldn't be downloaded from {list}: {ex.Message}", ex);
      }

      foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
      {
        if (!IPNetwork.TryParse(line, out var range))
        {
          throw Refusal($"{list} answered '{line}', which isn't an address range.");
        }

        ranges.Add(range);
      }
    }

    return ranges.Count > 0 ? ranges : throw Refusal("its lists of address ranges came back empty.");
  }

  private static InvalidOperationException Refusal(string reason, Exception? inner = null) =>
    new($"{ReverseProxyOptions.Key(nameof(ReverseProxyOptions.TrustCloudflare))} is on, but {reason}", inner);
}
