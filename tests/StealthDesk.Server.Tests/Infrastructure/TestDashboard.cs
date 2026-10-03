using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using StealthDesk.Contracts.Realtime;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>
/// A browser stand-in: JSON, as a browser would use, over long polling because the in-memory server can't
/// upgrade to WebSockets. Keeps every <see cref="IDashboardCallbacks.DeviceChanged"/> it receives, in order.
/// </summary>
public sealed class TestDashboard : IAsyncDisposable
{
  private readonly Channel<DeviceSummary> _changes = Channel.CreateUnbounded<DeviceSummary>();

  private TestDashboard(HubConnection connection)
  {
    Connection = connection;
    Connection.On<DeviceSummary>(nameof(IDashboardCallbacks.DeviceChanged), device => _changes.Writer.TryWrite(device));
  }

  public HubConnection Connection { get; }

  /// <param name="cookie">A session cookie from <see cref="TestAccounts.SignInForCookieAsync"/>, to connect as that user.</param>
  public static async Task<TestDashboard> ConnectAsync(ServerHost server, string? cookie = null)
  {
    var connection = new HubConnectionBuilder()
      .WithUrl(new Uri(server.Server.BaseAddress, Routes.Dashboard), options =>
      {
        options.HttpMessageHandlerFactory = _ => server.Server.CreateHandler();
        options.Transports = HttpTransportType.LongPolling;
        if (cookie is not null)
        {
          options.Headers["Cookie"] = cookie;
        }
      })
      .Build();

    var dashboard = new TestDashboard(connection);
    await connection.StartAsync(TestContext.Current.CancellationToken);
    return dashboard;
  }

  /// <summary>The next change received; throws <see cref="TimeoutException"/> if none arrives in time (5 s by default).</summary>
  public Task<DeviceSummary> NextChangeAsync(TimeSpan? within = null) =>
    _changes.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
      .WaitAsync(within ?? TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

  public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
