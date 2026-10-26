using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Core.Security;
using StealthDesk.Realtime;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>
/// A minimal agent for tests: its own key pair, an installer key of the server's tenant that goes with its reports
/// unless a report names its own, and a realtime channel to the test server (MessagePack over long polling, because
/// the in-memory server can't upgrade to WebSockets).
/// </summary>
public sealed class TestAgent : IAsyncDisposable
{
  private readonly ServiceProvider _services;
  private readonly Uri _endpoint;
  private readonly Action<HttpConnectionOptions> _configure;

  private TestAgent(
    ServiceProvider services,
    IRealtimeChannel<IAgentGateway> channel,
    Uri endpoint,
    Action<HttpConnectionOptions> configure,
    SigningKeys keys,
    string? installerKey)
  {
    _services = services;
    _endpoint = endpoint;
    _configure = configure;
    Channel = channel;
    Keys = keys;
    InstallerKey = installerKey;
  }

  public IRealtimeChannel<IAgentGateway> Channel { get; }
  public SigningKeys Keys { get; }
  public MessageSigner Signer { get; } = new(TimeProvider.System);

  /// <summary>A persistent key of the server's tenant; none for an agent that was given no key.</summary>
  public string? InstallerKey { get; }

  /// <param name="withInstallerKey">False for an agent that was given no installer key.</param>
  /// <param name="through">A proxy the agent reaches the server through; none connects straight to it.</param>
  /// <param name="callbacks">What the agent answers when the server calls it; none refuses every call.</param>
  public static async Task<TestAgent> ConnectAsync(
    ServerHost server,
    SigningKeys? keys = null,
    bool withInstallerKey = true,
    TestProxy? through = null,
    IAgentCallbacks? callbacks = null)
  {
    // A key belongs to a tenant, as a server has once its first user registered.
    var installerKey = withInstallerKey ? await TestInstallerKeys.CreateAsync(server, await TestTenants.EnsureAsync(server)) : null;

    var services = new ServiceCollection()
      .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
      .AddTransient<IHubConnectionBuilder>(_ => new HubConnectionBuilder().AddMessagePackProtocol())
      .AddSingleton(callbacks ?? new RefusingAgent())
      .AddRealtimeChannel<IAgentGateway, IAgentCallbacks, RefusingAgent>()
      .BuildServiceProvider();

    var channel = services.GetRequiredService<IRealtimeChannel<IAgentGateway>>();
    var endpoint = new Uri(server.Server.BaseAddress, Routes.AgentGateway);
    void Configure(HttpConnectionOptions options)
    {
      options.HttpMessageHandlerFactory = _ => through?.Handler(server) ?? server.Server.CreateHandler();
      options.Transports = HttpTransportType.LongPolling;
    }

    var opened = await channel.OpenAsync(endpoint, Configure, TestContext.Current.CancellationToken);

    Assert.True(opened, "The test agent could not reach the gateway.");
    return new TestAgent(services, channel, endpoint, Configure, keys ?? new MessageSigner(TimeProvider.System).CreateKeys(), installerKey);
  }

  /// <summary>Closes the connection and opens a new one, as the agent does when the server was gone for good.</summary>
  public async Task ReconnectAsync()
  {
    await Channel.CloseAsync();
    Assert.True(await Channel.OpenAsync(_endpoint, _configure, TestContext.Current.CancellationToken), "The test agent could not reconnect.");
  }

  public Task<GatewayReply<ReportReceipt>> ReportAsync(DeviceReport report) =>
    Channel.Server.SubmitReport(Sign(report));

  /// <summary>The report signed with this agent's key, carrying its installer key unless it names one.</summary>
  public SignedEnvelope<DeviceReport> Sign(DeviceReport report) =>
    Signer.Sign(report with { InstallerKey = report.InstallerKey ?? InstallerKey }, Keys.PrivateKey);

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
}
