using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StealthDesk.Agent.Core.Heartbeat;
using StealthDesk.Agent.Core.Settings;
using StealthDesk.Agent.Tests.Fakes;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Core.Security;

namespace StealthDesk.Agent.Tests;

public sealed class HeartbeatServiceTests : IDisposable
{
  private static readonly TimeSpan _interval = TimeSpan.FromMinutes(5);

  private readonly TempAgentFolder _folder = new();
  private readonly FakeChannel _channel = new();
  private readonly WatchedClock _clock = new();

  public void Dispose() => _folder.Dispose();

  [Fact]
  public async Task FirstAnswer_IsNull_OnceTheServerAcceptsTheDevice()
  {
    using var heartbeat = Create(_folder.OpenStore());
    Assert.False(heartbeat.FirstAnswer.IsCompleted);

    await heartbeat.SendNowAsync(TestContext.Current.CancellationToken);

    Assert.True(heartbeat.FirstAnswer.IsCompletedSuccessfully);
    Assert.Null(await heartbeat.FirstAnswer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task FirstAnswer_IsTheReason_WhenTheServerRefusesTheDevice()
  {
    _channel.Answer = _ => GatewayReply.Refuse<ReportReceipt>("The installer key is not valid.");
    using var heartbeat = Create(_folder.OpenStore());

    await heartbeat.SendNowAsync(TestContext.Current.CancellationToken);

    Assert.Equal("The installer key is not valid.", await heartbeat.FirstAnswer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task FirstAnswer_StaysTheFirst_WhateverComesNext()
  {
    var answers = new Queue<GatewayReply<ReportReceipt>>([
      GatewayReply.Refuse<ReportReceipt>("No tenant available."),
      GatewayReply.Accept(new ReportReceipt(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow)),
    ]);
    _channel.Answer = _ => answers.Dequeue();
    using var heartbeat = Create(_folder.OpenStore());

    await heartbeat.SendNowAsync(TestContext.Current.CancellationToken);
    await heartbeat.SendNowAsync(TestContext.Current.CancellationToken);

    Assert.Equal("No tenant available.", await heartbeat.FirstAnswer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task Runs_OnceEveryInterval()
  {
    using var heartbeat = Create(_folder.OpenStore());

    await heartbeat.StartAsync(TestContext.Current.CancellationToken);
    await _clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    _clock.Advance(_interval - TimeSpan.FromSeconds(1));
    await Task.Delay(100, TestContext.Current.CancellationToken);
    Assert.Empty(_channel.Received);

    _clock.Advance(TimeSpan.FromSeconds(1));
    Assert.True(await Wait.UntilAsync(() => _channel.Received.Count == 1), "No heartbeat after one interval.");

    _clock.Advance(_interval);
    Assert.True(await Wait.UntilAsync(() => _channel.Received.Count == 2), "No heartbeat after two intervals.");

    await heartbeat.StopAsync(TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task FirstHeartbeat_CreatesTheKeyOnce_AndKeepsSigningWithIt()
  {
    var store = _folder.OpenStore();
    using var heartbeat = Create(store);

    await heartbeat.SendNowAsync(TestContext.Current.CancellationToken);
    var savedKey = store.Current.PrivateKey;
    await heartbeat.SendNowAsync(TestContext.Current.CancellationToken);

    Assert.False(string.IsNullOrWhiteSpace(savedKey));
    Assert.Equal(savedKey, store.Current.PrivateKey);
    Assert.Equal(savedKey, _folder.OpenStore().Current.PrivateKey);

    var expectedSigner = new MessageSigner(TimeProvider.System).PublicKeyOf(savedKey!);
    Assert.All(_channel.Received, envelope => Assert.Equal(expectedSigner, envelope.SignerKey));
  }

  [Fact]
  public async Task Heartbeat_IsSignedSoTheServerCanVerifyIt()
  {
    using var heartbeat = Create(_folder.OpenStore());

    await heartbeat.SendNowAsync(TestContext.Current.CancellationToken);

    var envelope = Assert.Single(_channel.Received);
    Assert.True(new MessageSigner(TimeProvider.System).HasValidSignature(envelope, envelope.SignerKey));
    Assert.Equal("TEST-PC", envelope.Payload.MachineName);
  }

  [Fact]
  public async Task IdentityAssignedByTheServer_IsSavedAndUsedAfterwards()
  {
    var assignedDevice = Guid.NewGuid();
    var assignedTenant = Guid.NewGuid();
    _channel.Answer = _ => GatewayReply.Accept(new ReportReceipt(assignedDevice, assignedTenant, DateTimeOffset.UtcNow));
    var store = _folder.OpenStore();
    using var heartbeat = Create(store);

    await heartbeat.SendNowAsync(TestContext.Current.CancellationToken);
    await heartbeat.SendNowAsync(TestContext.Current.CancellationToken);

    Assert.Equal(Guid.Empty, _channel.Received[0].Payload.DeviceId);
    Assert.Equal(assignedDevice, _channel.Received[1].Payload.DeviceId);
    Assert.Equal(assignedTenant, _channel.Received[1].Payload.TenantId);

    var afterRestart = _folder.OpenStore().Current;
    Assert.Equal(assignedDevice, afterRestart.DeviceId);
    Assert.Equal(assignedTenant, afterRestart.TenantId);
  }

  [Fact]
  public async Task RefusedHeartbeat_DoesNotChangeTheIdentity()
  {
    _channel.Answer = _ => GatewayReply.Refuse<ReportReceipt>("Signature verification failed.");
    var store = _folder.OpenStore();
    using var heartbeat = Create(store);

    await heartbeat.SendNowAsync(TestContext.Current.CancellationToken);

    Assert.Equal(Guid.Empty, store.Current.DeviceId);
  }

  [Fact]
  public async Task InstallerKey_GoesWithEveryReport_UntilOneIsAccepted()
  {
    var answers = new Queue<GatewayReply<ReportReceipt>>([
      GatewayReply.Refuse<ReportReceipt>("No tenant available."),
      GatewayReply.Accept(new ReportReceipt(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow)),
    ]);
    _channel.Answer = _ => answers.TryDequeue(out var answer) ? answer : GatewayReply.Accept(new ReportReceipt(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow));
    using var heartbeat = Create(_folder.OpenStore(new AgentSettings { InstallerKey = "key-id:secret" }));

    for (var i = 0; i < 3; i++)
    {
      await heartbeat.SendNowAsync(TestContext.Current.CancellationToken);
    }

    Assert.Equal(["key-id:secret", "key-id:secret", null], _channel.Received.Select(x => x.Payload.InstallerKey));
  }

  [Fact]
  public async Task InstallerKey_IsNeverWrittenToTheSettingsFile()
  {
    var store = _folder.OpenStore(new AgentSettings { InstallerKey = "key-id:secret" });
    using var heartbeat = Create(store);

    await heartbeat.SendNowAsync(TestContext.Current.CancellationToken);

    Assert.Equal("key-id:secret", store.Current.InstallerKey);
    Assert.False(string.IsNullOrEmpty(store.Current.PrivateKey));
    Assert.DoesNotContain("secret", await File.ReadAllTextAsync(_folder.Paths.SettingsFile, TestContext.Current.CancellationToken));
    Assert.Null(_folder.OpenStore().Current.InstallerKey);
  }

  [Fact]
  public async Task WhileDisconnected_NothingIsSent()
  {
    _channel.State = HubConnectionState.Reconnecting;
    using var heartbeat = Create(_folder.OpenStore());

    await heartbeat.SendNowAsync(TestContext.Current.CancellationToken);

    Assert.Empty(_channel.Received);
  }

  private HeartbeatService Create(ISettingsStore store) => new(
    _channel,
    new FakeInventory(),
    store,
    new MessageSigner(TimeProvider.System),
    Options.Create(new HeartbeatOptions { Interval = _interval }),
    _clock,
    NullLogger<HeartbeatService>.Instance);
}
