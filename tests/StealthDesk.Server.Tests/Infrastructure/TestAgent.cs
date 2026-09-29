using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Core.Security;
using StealthDesk.Realtime;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>
/// A minimal agent for tests: its own key pair and a realtime channel to the test server
/// (MessagePack over long polling, because the in-memory server can't upgrade to WebSockets).
/// </summary>
public sealed class TestAgent : IAsyncDisposable
{
  private readonly ServiceProvider _services;

  private TestAgent(ServiceProvider services, IRealtimeChannel<IAgentGateway> channel, SigningKeys keys)
  {
    _services = services;
    Channel = channel;
    Keys = keys;
  }

  public IRealtimeChannel<IAgentGateway> Channel { get; }
  public SigningKeys Keys { get; }
  public MessageSigner Signer { get; } = new(TimeProvider.System);

  public static async Task<TestAgent> ConnectAsync(ServerHost server, SigningKeys? keys = null)
  {
    var services = new ServiceCollection()
      .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
      .AddTransient<IHubConnectionBuilder>(_ => new HubConnectionBuilder().AddMessagePackProtocol())
      .AddRealtimeChannel<IAgentGateway, IAgentCallbacks, NoCallbacks>()
      .BuildServiceProvider();

    var channel = services.GetRequiredService<IRealtimeChannel<IAgentGateway>>();
    var endpoint = new Uri(server.Server.BaseAddress, Routes.AgentGateway);

    var opened = await channel.OpenAsync(endpoint, options =>
    {
      options.HttpMessageHandlerFactory = _ => server.Server.CreateHandler();
      options.Transports = HttpTransportType.LongPolling;
    }, TestContext.Current.CancellationToken);

    Assert.True(opened, "The test agent could not reach the gateway.");
    return new TestAgent(services, channel, keys ?? new MessageSigner(TimeProvider.System).CreateKeys());
  }

  public Task<GatewayReply<ReportReceipt>> ReportAsync(DeviceReport report) =>
    Channel.Server.SubmitReport(Signer.Sign(report, Keys.PrivateKey));

  public Task<GatewayReply<ReportReceipt>> SubmitAsync(SignedEnvelope<DeviceReport> envelope) =>
    Channel.Server.SubmitReport(envelope);

  public async ValueTask DisposeAsync()
  {
    await Channel.DisposeAsync();
    await _services.DisposeAsync();
  }

  public static DeviceReport Report(Guid deviceId, string machineName = "OFFICE-PC") => new()
  {
    DeviceId = deviceId,
    MachineName = machineName,
    DnsName = $"{machineName.ToLowerInvariant()}.corp.local",
    AgentVersion = "0.1.0",
    Platform = DevicePlatform.Windows,
    OsDescription = "Microsoft Windows 11 Pro",
    OsArchitecture = System.Runtime.InteropServices.Architecture.X64,
    Is64BitOs = true,
    CpuCores = 8,
    CpuLoad = 0.12,
    MemoryTotalGb = 16,
    MemoryUsedGb = 7.5,
    StorageTotalGb = 512,
    StorageUsedGb = 210,
    LoggedOnUsers = ["alice"],
    MacAddresses = ["00155D012345"],
    LocalIpV4 = "10.0.0.20",
    Disks = [new DiskInfo { Name = @"C:\", Format = "NTFS", SizeGb = 512, FreeGb = 302 }],
  };

  private sealed class NoCallbacks : IAgentCallbacks;
}
