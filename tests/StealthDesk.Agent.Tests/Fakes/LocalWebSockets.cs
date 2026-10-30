using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace StealthDesk.Agent.Tests.Fakes;

/// <summary>Two ends of a WebSocket over a local socket, standing for the relay and the device's end.</summary>
internal static class LocalWebSockets
{
  /// <param name="disposables">Takes what the caller disposes once done, sockets included.</param>
  public static async Task<(WebSocket Server, WebSocket Client)> ConnectAsync(List<IDisposable> disposables, CancellationToken cancellationToken)
  {
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var client = new TcpClient();
    disposables.Add(client);
    var accepting = listener.AcceptTcpClientAsync(cancellationToken);
    await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
    var server = await accepting;
    disposables.Add(server);
    var serverSocket = WebSocket.CreateFromStream(server.GetStream(), isServer: true, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
    var clientSocket = WebSocket.CreateFromStream(client.GetStream(), isServer: false, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
    disposables.Add(serverSocket);
    return (serverSocket, clientSocket);
  }
}
