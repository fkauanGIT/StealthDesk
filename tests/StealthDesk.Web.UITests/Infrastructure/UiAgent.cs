using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Core.Security;
using StealthDesk.Realtime;

namespace StealthDesk.Web.UITests.Infrastructure;

/// <summary>
/// An agent for browser tests: the real agent's channel and signed reports over the network, with a made-up machine,
/// so it runs on any operating system.
/// </summary>
public sealed class UiAgent : IAsyncDisposable
{
  private readonly ServiceProvider _services;
  private readonly IRealtimeChannel<IAgentGateway> _channel;
  private readonly MessageSigner _signer = new(TimeProvider.System);
  private readonly SigningKeys _keys;

  private UiAgent(ServiceProvider services, IRealtimeChannel<IAgentGateway> channel, string machineName)
  {
    _services = services;
    _channel = channel;
    _keys = _signer.CreateKeys();
    MachineName = machineName;
  }

  public Guid DeviceId { get; } = Guid.NewGuid();

  public string MachineName { get; }

  /// <summary>Connects to the server and reports the machine once, as an agent does when it starts.</summary>
  public static async Task<UiAgent> StartAsync(UiServer server, string machineName)
  {
    var services = new ServiceCollection()
      .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
      .AddTransient<IHubConnectionBuilder>(_ => new HubConnectionBuilder().AddMessagePackProtocol())
      .AddRealtimeChannel<IAgentGateway, IAgentCallbacks, NoCallbacks>()
      .BuildServiceProvider();

    var channel = services.GetRequiredService<IRealtimeChannel<IAgentGateway>>();
    if (!await channel.OpenAsync(new Uri(server.BaseAddress, Routes.AgentGateway.TrimStart('/')), _ => { }, TestContext.Current.CancellationToken))
    {
      throw new InvalidOperationException("The test agent could not reach the server.");
    }

    var agent = new UiAgent(services, channel, machineName);
    var reply = await channel.Server.SubmitReport(agent._signer.Sign(agent.Report(), agent._keys.PrivateKey));
    if (!reply.Accepted)
    {
      throw new InvalidOperationException($"The server refused the test agent's report: {reply.Error}");
    }

    return agent;
  }

  /// <summary>Disconnects, as when the machine shuts down: the server marks the device offline.</summary>
  public async ValueTask DisposeAsync()
  {
    await _channel.DisposeAsync();
    await _services.DisposeAsync();
  }

  private DeviceReport Report() => new()
  {
    DeviceId = DeviceId,
    MachineName = MachineName,
    AgentVersion = "0.4.0",
    Platform = DevicePlatform.Windows,
    OsDescription = "Microsoft Windows 11 Pro",
    OsArchitecture = System.Runtime.InteropServices.Architecture.X64,
    Is64BitOs = true,
    CpuCores = 8,
    CpuLoad = 0.2,
    MemoryTotalGb = 16,
    MemoryUsedGb = 6,
    StorageTotalGb = 512,
    StorageUsedGb = 200,
    LoggedOnUsers = ["alice"],
    Disks = [new DiskInfo { Name = @"C:\", Format = "NTFS", SizeGb = 512, FreeGb = 312 }],
  };

  private sealed class NoCallbacks : IAgentCallbacks;
}
