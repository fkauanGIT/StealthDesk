using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>
/// Requests that reach the server from a chosen address, with the headers a reverse proxy adds. The in-memory server
/// gives requests no address of their own; <see cref="AddTo"/> sets the one each request names, before the server's
/// own pipeline reads it.
/// </summary>
public sealed class TestProxy(string address)
{
  private const string PeerHeader = "X-Test-Peer";

  /// <summary>Where the proxy connects to the server from.</summary>
  public string Address { get; } = address;

  /// <summary>What the proxy forwards; a null value isn't sent.</summary>
  public string? ForwardedFor { get; init; }
  public string? ForwardedProto { get; init; }
  public string? ForwardedHost { get; init; }

  public static ServerHost AddTo(ServerHost server) =>
    server.WithServices(services => services.AddSingleton<IStartupFilter, PeerFilter>());

  /// <summary>A handler for an <see cref="HttpClient"/> or a hub connection that sends through this proxy.</summary>
  public HttpMessageHandler Handler(ServerHost server) => new Forwarding(this) { InnerHandler = server.Server.CreateHandler() };

  /// <summary>A client that keeps no cookies, like <see cref="TestAccounts.Client"/>, and doesn't follow redirects.</summary>
  public HttpClient Client(ServerHost server) => new(Handler(server)) { BaseAddress = server.Server.BaseAddress };

  private sealed class Forwarding(TestProxy proxy) : DelegatingHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      request.Headers.Add(PeerHeader, proxy.Address);
      Add(request, "X-Forwarded-For", proxy.ForwardedFor);
      Add(request, "X-Forwarded-Proto", proxy.ForwardedProto);
      Add(request, "X-Forwarded-Host", proxy.ForwardedHost);
      return base.SendAsync(request, cancellationToken);
    }

    private static void Add(HttpRequestMessage request, string name, string? value)
    {
      if (value is not null)
      {
        request.Headers.Add(name, value);
      }
    }
  }

  private sealed class PeerFilter : IStartupFilter
  {
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
      app.Use((context, call) =>
      {
        if (IPAddress.TryParse(context.Request.Headers[PeerHeader], out var peer))
        {
          context.Connection.RemoteIpAddress = peer;
        }

        return call(context);
      });
      next(app);
    };
  }
}
