using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Contracts.Terminal;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>
/// A browser stand-in: JSON, as a browser would use, over long polling because the in-memory server can't
/// upgrade to WebSockets. Keeps every <see cref="IDashboardCallbacks.DeviceChanged"/> and terminal output it receives,
/// in order.
/// </summary>
public sealed class TestDashboard : IAsyncDisposable
{
  private readonly Channel<DeviceSummary> _changes = Channel.CreateUnbounded<DeviceSummary>();
  private readonly List<TerminalOutput> _terminalOutput = [];

  private TestDashboard(HubConnection connection)
  {
    Connection = connection;
    Connection.On<DeviceSummary>(nameof(IDashboardCallbacks.DeviceChanged), device => _changes.Writer.TryWrite(device));
    Connection.On<TerminalOutput>(nameof(IDashboardCallbacks.ReceiveTerminalOutput), output =>
    {
      lock (_terminalOutput)
      {
        _terminalOutput.Add(output);
      }
    });
  }

  public HubConnection Connection { get; }

  /// <summary>Every terminal output received so far, in order.</summary>
  public IReadOnlyList<TerminalOutput> TerminalOutput
  {
    get
    {
      lock (_terminalOutput)
      {
        return [.. _terminalOutput];
      }
    }
  }

  /// <summary>Connects as a new user of the test tenant, the one test agents join, who can read its devices.</summary>
  public static async Task<TestDashboard> ConnectSignedInAsync(ServerHost server)
  {
    var email = $"viewer-{Guid.NewGuid():N}@example.com";
    await TestPermissions.AllowTenantDevicesAsync(server, await TestAccounts.CreateUserAsync(server, email));
    return await ConnectAsync(server, await TestAccounts.SignInForCookieAsync(server, email));
  }

  /// <param name="cookie">A session cookie from <see cref="TestAccounts.SignInForCookieAsync"/>, to connect as that user.</param>
  /// <param name="headers">Other headers to connect with, e.g. a service account's key.</param>
  public static async Task<TestDashboard> ConnectAsync(ServerHost server, string? cookie = null, IReadOnlyDictionary<string, string>? headers = null)
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

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
          options.Headers[name] = value;
        }
      })
      .Build();

    var dashboard = new TestDashboard(connection);
    await connection.StartAsync(TestContext.Current.CancellationToken);
    // StartAsync returns once the handshake is answered, before the server adds the connection to the ones
    // Clients.All reaches and runs OnConnectedAsync. Calls are only answered after both, so a round trip makes
    // the connection fully set up before a test sends to it. Unsubscribing from nothing changes nothing.
    await connection.InvokeAsync(nameof(IDashboardHub.UnsubscribeFromDevices), Array.Empty<Guid>(), TestContext.Current.CancellationToken);
    return dashboard;
  }

  /// <summary>Asks for these devices' changes, as the web client does for the devices it shows; returns the ids accepted.</summary>
  public async Task<IReadOnlyList<Guid>> SubscribeAsync(params Guid[] deviceIds)
  {
    var reply = await Connection.InvokeAsync<GatewayReply<IReadOnlyList<Guid>>>(
      nameof(IDashboardHub.SubscribeToDevices), deviceIds, TestContext.Current.CancellationToken);
    Assert.True(reply.Accepted, reply.Error);
    return reply.Value!;
  }

  /// <summary>The next change received; throws <see cref="TimeoutException"/> if none arrives in time (5 s by default).</summary>
  public Task<DeviceSummary> NextChangeAsync(TimeSpan? within = null) =>
    _changes.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
      .WaitAsync(within ?? TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

  public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
