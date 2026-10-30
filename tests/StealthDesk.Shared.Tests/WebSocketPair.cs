using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.RemoteControl;

namespace StealthDesk.Shared.Tests;

/// <summary>Two ends of a WebSocket over a local socket, standing for the two ends of a relay session.</summary>
internal sealed class WebSocketPair : IDisposable
{
  private readonly List<IDisposable> _disposables = [];

  private WebSocketPair()
  {
  }

  public WebSocket Client { get; private set; } = null!;

  public WebSocket Server { get; private set; } = null!;

  public static async Task<WebSocketPair> ConnectAsync(CancellationToken cancellationToken)
  {
    var pair = new WebSocketPair();
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var client = new TcpClient();
    pair._disposables.Add(client);
    var accepting = listener.AcceptTcpClientAsync(cancellationToken);
    await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
    var server = await accepting;
    pair._disposables.Add(server);
    pair.Client = WebSocket.CreateFromStream(client.GetStream(), isServer: false, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
    pair.Server = WebSocket.CreateFromStream(server.GetStream(), isServer: true, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
    pair._disposables.Add(pair.Client);
    pair._disposables.Add(pair.Server);
    return pair;
  }

  public static RelayStream Stream(WebSocket socket, TimeProvider? clock = null) =>
    new(socket, clock ?? TimeProvider.System, NullLogger.Instance);

  public void Dispose() => _disposables.ForEach(x => x.Dispose());
}
