using System.Diagnostics;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Gateway;

namespace StealthDesk.Server.Tests;

/// <summary>
/// End to end: the server calling a test agent over the connection the agent opened, and waiting for its answer. The
/// tests that reconnect need Docker.
/// </summary>
public class AgentCallTests
{
  [Fact]
  public async Task CallingAnOnlineDevice_GetsItsAgentsAnswer()
  {
    using var server = ServerHost.InMemory();
    await using var agent = await TestAgent.ConnectAsync(server, callbacks: new AnsweringAgent("1.2.3"));
    var deviceId = await ReportAsync(agent);

    var reply = await CallAsync(server, deviceId);

    Assert.True(reply.Accepted, reply.Error);
    Assert.Equal("1.2.3", reply.Value);
  }

  [Fact]
  public async Task CallingAnOfflineDevice_FailsAtOnce()
  {
    using var server = ServerHost.InMemory();
    var agent = await TestAgent.ConnectAsync(server, callbacks: new AnsweringAgent("1.2.3"));
    var deviceId = await ReportAsync(agent);
    await agent.DisposeAsync();
    Assert.True(await Eventually.TrueAsync(() => server.WithDbAsync(async db => !(await db.Devices.SingleAsync(x => x.Id == deviceId)).IsOnline)));

    var watch = Stopwatch.StartNew();
    var reply = await CallAsync(server, deviceId);

    Assert.False(reply.Accepted);
    Assert.Equal("The device is offline.", reply.Error);
    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Took {watch.Elapsed}, not at once.");
  }

  [Fact]
  public async Task CallingAnUnknownDevice_SaysItIsOffline()
  {
    using var server = ServerHost.InMemory();

    var reply = await CallAsync(server, Guid.NewGuid());

    Assert.False(reply.Accepted);
    Assert.Equal("The device is offline.", reply.Error);
  }

  [Fact]
  public async Task AnAgentThatNeverAnswers_FailsWithinTheTimeLimit()
  {
    using var server = ServerHost.InMemory().With("Gateway:AgentCallTimeout", "00:00:02");
    var silent = new SilentAgent();
    await using var agent = await TestAgent.ConnectAsync(server, callbacks: silent);
    var deviceId = await ReportAsync(agent);

    var watch = Stopwatch.StartNew();
    var reply = await CallAsync(server, deviceId);
    watch.Stop();
    silent.Release();

    Assert.True(silent.WasCalled, "The call never reached the agent.");
    Assert.False(reply.Accepted);
    Assert.Equal("The device didn't answer within 2 seconds.", reply.Error);
    Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1.9), TimeSpan.FromSeconds(10));
  }

  [Fact]
  public async Task AnAgentDisconnectingDuringACall_FailsWithoutWaitingForTheTimeLimit()
  {
    using var server = ServerHost.InMemory();
    var silent = new SilentAgent();
    var agent = await TestAgent.ConnectAsync(server, callbacks: silent);
    var deviceId = await ReportAsync(agent);

    var watch = Stopwatch.StartNew();
    var call = CallAsync(server, deviceId);
    Assert.True(await Eventually.TrueAsync(() => Task.FromResult(silent.WasCalled)), "The call never reached the agent.");
    await agent.DisposeAsync();
    var reply = await call;
    silent.Release();

    Assert.False(reply.Accepted);
    Assert.Equal("The device went offline before answering.", reply.Error);
    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"Took {watch.Elapsed}: it waited for the 30 second limit.");
  }

  [Fact]
  public async Task AnAgentFailingToAnswer_IsRefusedWithoutItsDetails()
  {
    using var server = ServerHost.InMemory();
    await using var agent = await TestAgent.ConnectAsync(server, callbacks: new FailingAgent());
    var deviceId = await ReportAsync(agent);

    var reply = await CallAsync(server, deviceId);

    Assert.False(reply.Accepted);
    Assert.Equal("The device couldn't carry out the request.", reply.Error);
  }

  // On PostgreSQL: the old connection closing races the new one's report, which only a relational database settles.
  [Fact]
  public async Task AfterTheAgentReconnects_CallsReachTheNewConnection()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var agent = await TestAgent.ConnectAsync(server, callbacks: new AnsweringAgent("1.2.3"));
    var deviceId = await ReportAsync(agent);
    var before = await ConnectionOfAsync(server, deviceId);

    await agent.ReconnectAsync();
    // As the real agent does: a new connection reports at once.
    await ReportAsync(agent, deviceId);

    Assert.NotEqual(before, await ConnectionOfAsync(server, deviceId));
    var reply = await CallAsync(server, deviceId);
    Assert.True(reply.Accepted, reply.Error);
    Assert.Equal("1.2.3", reply.Value);
  }

  [Fact]
  public async Task ANewConnectionForTheSameDevice_TakesOverItsCalls()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var first = await TestAgent.ConnectAsync(server, callbacks: new AnsweringAgent("first"));
    var deviceId = await ReportAsync(first);
    await using var second = await TestAgent.ConnectAsync(server, keys: first.Keys, callbacks: new AnsweringAgent("second"));
    await ReportAsync(second, deviceId);

    Assert.Equal("second", (await CallAsync(server, deviceId)).Value);

    // The old connection closing doesn't take the device offline: the new one still answers.
    await first.DisposeAsync();
    await Task.Delay(500, TestContext.Current.CancellationToken);
    var reply = await CallAsync(server, deviceId);
    Assert.True(reply.Accepted, reply.Error);
    Assert.Equal("second", reply.Value);
  }

  private static async Task<Guid> ReportAsync(TestAgent agent, Guid? deviceId = null)
  {
    var reply = await agent.ReportAsync(TestAgent.Report(deviceId ?? Guid.NewGuid()));
    Assert.True(reply.Accepted, reply.Error);
    return reply.Value!.DeviceId;
  }

  private static async Task<GatewayReply<string>> CallAsync(ServerHost server, Guid deviceId)
  {
    using var scope = server.Services.CreateScope();
    var calls = scope.ServiceProvider.GetRequiredService<IAgentCalls>();
    return await calls.CallAsync(deviceId, (agent, token) => agent.GetAgentVersion(token), TestContext.Current.CancellationToken);
  }

  private static Task<string> ConnectionOfAsync(ServerHost server, Guid deviceId) =>
    server.WithDbAsync(async db => (await db.Devices.SingleAsync(x => x.Id == deviceId)).ConnectionId);

  private sealed class AnsweringAgent(string version) : RefusingAgent
  {
    public override Task<GatewayReply<string>> GetAgentVersion(CancellationToken cancellationToken) =>
      Task.FromResult(GatewayReply.Accept(version));
  }

  /// <summary>Takes the call and never answers it until the test lets go.</summary>
  private sealed class SilentAgent : RefusingAgent
  {
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool WasCalled { get; private set; }

    public void Release() => _released.TrySetResult();

    public override async Task<GatewayReply<string>> GetAgentVersion(CancellationToken cancellationToken)
    {
      WasCalled = true;
      await _released.Task;
      return GatewayReply.Refuse<string>("Too late.");
    }
  }

  private sealed class FailingAgent : RefusingAgent
  {
    public override Task<GatewayReply<string>> GetAgentVersion(CancellationToken cancellationToken) =>
      throw new InvalidOperationException("C:\\secret\\path is locked");
  }
}
