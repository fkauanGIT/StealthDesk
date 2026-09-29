using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StealthDesk.Agent.Core.Connection;
using StealthDesk.Agent.Core.Heartbeat;
using StealthDesk.Agent.Core.Settings;
using StealthDesk.Agent.Tests.Fakes;
using StealthDesk.Core;

namespace StealthDesk.Agent.Tests;

public sealed class GatewayConnectorTests : IDisposable
{
  private readonly TempAgentFolder _folder = new();
  private readonly FakeChannel _channel = new();
  private readonly CountingHeartbeat _heartbeat = new();
  private readonly FakeTimeProvider _clock = new();

  public void Dispose() => _folder.Dispose();

  [Fact]
  public async Task KeepsRetrying_UntilTheServerAnswers_ThenSendsAHeartbeat()
  {
    _channel.FailOpens(3);
    using var connector = Create(_folder.OpenStore());

    await connector.StartAsync(TestContext.Current.CancellationToken);

    // Each failed attempt waits (2s, 4s, 8s with no spread); move the clock until the fourth attempt connects.
    for (var i = 0; i < 20 && _channel.OpenAttempts < 4; i++)
    {
      _clock.Advance(TimeSpan.FromSeconds(8));
      await Task.Delay(20, TestContext.Current.CancellationToken);
    }

    Assert.True(await Wait.UntilAsync(() => _heartbeat.Sent == 1), "No heartbeat after connecting.");
    Assert.Equal(4, _channel.OpenAttempts);
    Assert.All(_channel.OpenedEndpoints, endpoint => Assert.Equal("http://server.test/hubs/agent", endpoint.ToString()));

    await connector.StopAsync(TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task AfterTheConnectionComesBack_SendsAHeartbeatRightAway()
  {
    using var connector = Create(_folder.OpenStore());
    await connector.StartAsync(TestContext.Current.CancellationToken);
    Assert.True(await Wait.UntilAsync(() => _heartbeat.Sent == 1));

    await _channel.SimulateRestoredAsync();

    Assert.Equal(2, _heartbeat.Sent);
    await connector.StopAsync(TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task WithoutAServerUrl_DoesNotTryToConnect()
  {
    var store = new SettingsStore(Microsoft.Extensions.Options.Options.Create(new AgentSettings()), _folder.Paths);
    using var connector = Create(store);

    await connector.StartAsync(TestContext.Current.CancellationToken);
    await Task.Delay(100, TestContext.Current.CancellationToken);

    Assert.Equal(0, _channel.OpenAttempts);
    await connector.StopAsync(TestContext.Current.CancellationToken);
  }

  private GatewayConnector Create(ISettingsStore store) => new(
    _channel,
    _heartbeat,
    store,
    new Backoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), TimeSpan.Zero),
    _clock,
    NullLogger<GatewayConnector>.Instance);

  private sealed class CountingHeartbeat : IHeartbeat
  {
    private int _sent;

    public int Sent => Volatile.Read(ref _sent);

    public Task SendNowAsync(CancellationToken cancellationToken = default)
    {
      Interlocked.Increment(ref _sent);
      return Task.CompletedTask;
    }
  }
}
