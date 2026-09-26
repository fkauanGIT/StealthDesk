using System.Runtime.InteropServices;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using StealthDesk.Agent.Common.Services;
using StealthDesk.Agent.Shared.Interfaces;
using StealthDesk.Agent.Shared.Services;
using StealthDesk.Libraries.Api.Contracts.Dtos;
using StealthDesk.Libraries.Api.Contracts.Dtos.HubDtos;
using StealthDesk.Libraries.Api.Contracts.Dtos.ServerApi.Internal;
using StealthDesk.Libraries.Api.Contracts.Enums;
using StealthDesk.Libraries.Api.Contracts.Hubs;
using StealthDesk.Libraries.Shared.Services;
using StealthDesk.Libraries.Shared.Services.Encryption;
using StealthDesk.Libraries.Signalr.Client;

namespace StealthDesk.Agent.Common.Tests;

public class AgentHeartbeatTimerTests
{
  [Fact]
  public async Task ExecuteAsync_SendsHeartbeatWhenThePeriodElapses()
  {
    var timeProvider = new SignalingTimeProvider(DateTimeOffset.UtcNow);
    var agent = new FakeAgent(timeProvider);
    agent.StoredPrivateKey = Convert.ToBase64String(agent.KeyProvider.GenerateKeyPair().PrivateKey);
    using var timer = agent.CreateTimer();

    await timer.StartAsync(TestContext.Current.CancellationToken);

    // ExecuteAsync runs in the background, so the clock may only move once its PeriodicTimer exists.
    await timeProvider.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    timeProvider.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
    await Task.Delay(100, TestContext.Current.CancellationToken);
    Assert.Empty(agent.SentHeartbeats);

    timeProvider.Advance(TimeSpan.FromSeconds(1));
    Assert.True(await WaitUntil(() => agent.SentHeartbeats.Count == 1), "No heartbeat after the first period.");

    timeProvider.Advance(TimeSpan.FromMinutes(5));
    Assert.True(await WaitUntil(() => agent.SentHeartbeats.Count == 2), "No heartbeat after the second period.");

    await timer.StopAsync(TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task SendDeviceHeartbeat_GeneratesAndPersistsMissingPrivateKeyOnce()
  {
    var agent = new FakeAgent(TimeProvider.System);
    using var timer = agent.CreateTimer();

    await timer.SendDeviceHeartbeat();
    await timer.SendDeviceHeartbeat();

    Assert.Equal(1, agent.UpdatePrivateKeyCalls);
    Assert.NotNull(agent.StoredPrivateKey);
    Assert.Equal(2, agent.SentHeartbeats.Count);

    // Both heartbeats carry the public key of the persisted private key.
    var expectedPublicKey = agent.KeyProvider.DerivePublicKeyBase64(agent.StoredPrivateKey);
    Assert.All(agent.SentHeartbeats, heartbeat => Assert.Equal(expectedPublicKey, heartbeat.PublicKey));
  }

  private static async Task<bool> WaitUntil(Func<bool> condition)
  {
    var timeout = TimeSpan.FromSeconds(5);
    var started = DateTime.UtcNow;

    while (DateTime.UtcNow - started < timeout)
    {
      if (condition())
      {
        return true;
      }

      await Task.Delay(20, TestContext.Current.CancellationToken);
    }

    return false;
  }

  private sealed class SignalingTimeProvider(DateTimeOffset startDateTime) : FakeTimeProvider(startDateTime)
  {
    private readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task TimerCreated => _timerCreated.Task;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
      var timer = base.CreateTimer(callback, state, dueTime, period);
      _timerCreated.TrySetResult();
      return timer;
    }
  }

  private sealed class FakeAgent
  {
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly TimeProvider _timeProvider;
    private int _updatePrivateKeyCalls;

    public FakeAgent(TimeProvider timeProvider)
    {
      _timeProvider = timeProvider;
      KeyProvider = new Ed25519KeyProvider(TimeProvider.System, NullLogger<Ed25519KeyProvider>.Instance);
    }

    public Ed25519KeyProvider KeyProvider { get; }
    public List<SignedDto<DeviceUpdateRequestDto>> SentHeartbeats { get; } = [];
    public string? StoredPrivateKey { get; set; }
    public int UpdatePrivateKeyCalls => _updatePrivateKeyCalls;

    public AgentHeartbeatTimer CreateTimer()
    {
      var hub = new Mock<IAgentHub>();
      hub
        .Setup(x => x.UpdateDeviceSigned(It.IsAny<SignedDto<DeviceUpdateRequestDto>>()))
        .Callback<SignedDto<DeviceUpdateRequestDto>>(signed =>
        {
          lock (SentHeartbeats)
          {
            SentHeartbeats.Add(signed);
          }
        })
        .ReturnsAsync(HubResult.Ok(CreateResponse()));

      var hubConnection = new Mock<IHubConnection<IAgentHub>>();
      hubConnection.SetupGet(x => x.ConnectionState).Returns(HubConnectionState.Connected);
      hubConnection.SetupGet(x => x.Server).Returns(hub.Object);

      var systemEnvironment = new Mock<ISystemEnvironment>();
      systemEnvironment.SetupGet(x => x.IsDebug).Returns(false);

      var deviceInfo = new Mock<IDeviceInfoProvider>();
      deviceInfo.Setup(x => x.GetDeviceInfo()).ReturnsAsync(CreateDeviceDto);

      var optionsAccessor = new Mock<IOptionsAccessor>();
      optionsAccessor.SetupGet(x => x.PrivateKey).Returns(() => StoredPrivateKey);
      optionsAccessor
        .Setup(x => x.UpdatePrivateKey(It.IsAny<string>()))
        .Callback<string>(key =>
        {
          StoredPrivateKey = key;
          Interlocked.Increment(ref _updatePrivateKeyCalls);
        })
        .Returns(Task.CompletedTask);

      return new AgentHeartbeatTimer(
        _timeProvider,
        hubConnection.Object,
        systemEnvironment.Object,
        deviceInfo.Object,
        optionsAccessor.Object,
        KeyProvider,
        NullLogger<AgentHeartbeatTimer>.Instance);
    }

    private DeviceUpdateRequestDto CreateDeviceDto()
    {
      return new DeviceUpdateRequestDto(
        Id: _deviceId,
        TenantId: Guid.Empty,
        Name: "TEST-PC",
        AgentVersion: "1.0.0",
        Is64Bit: true,
        OsArchitecture: Architecture.X64,
        OsDescription: "Windows 11",
        Platform: SystemPlatform.Windows,
        ProcessorCount: 8,
        CpuUtilization: 0.25,
        TotalMemory: 16,
        TotalStorage: 512,
        UsedMemory: 8,
        UsedStorage: 256,
        CurrentUsers: [],
        MacAddresses: [],
        LocalIpV4: "10.0.0.2",
        LocalIpV6: "",
        Drives: []);
    }

    private DeviceResponseDto CreateResponse()
    {
      return new DeviceResponseDto(
        Name: "TEST-PC",
        AgentVersion: "1.0.0",
        CpuUtilization: 0.25,
        Id: _deviceId,
        Is64Bit: true,
        IsOnline: true,
        LastSeen: DateTimeOffset.UtcNow,
        OsArchitecture: Architecture.X64,
        Platform: SystemPlatform.Windows,
        ProcessorCount: 8,
        ConnectionId: "connection",
        OsDescription: "Windows 11",
        TenantId: Guid.Empty,
        TotalMemory: 16,
        TotalStorage: 512,
        UsedMemory: 8,
        UsedStorage: 256,
        CurrentUsers: [],
        MacAddresses: [],
        PublicIpV4: "",
        PublicIpV6: "",
        LocalIpV4: "10.0.0.2",
        LocalIpV6: "",
        Drives: [],
        IsOutdated: false);
    }
  }
}
