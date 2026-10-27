using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Core.Security;
using Npgsql;
using StealthDesk.Realtime;
using StealthDesk.Web.Server.Persistence;

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

  private UiAgent(ServiceProvider services, IRealtimeChannel<IAgentGateway> channel, string machineName, AgentIdentity? identity)
  {
    _services = services;
    _channel = channel;
    _keys = identity is null ? _signer.CreateKeys() : new SigningKeys(_signer.PublicKeyOf(identity.PrivateKey), identity.PrivateKey);
    DeviceId = identity?.DeviceId ?? Guid.NewGuid();
    MachineName = machineName;
  }

  public Guid DeviceId { get; }

  public string MachineName { get; }

  /// <summary>What the agent would keep on disk: with it, the same device reports again, without an installer key.</summary>
  public AgentIdentity Identity => new(DeviceId, _keys.PrivateKey);

  /// <summary>Connects to the server and reports the machine once, as an agent does when it starts.</summary>
  /// <param name="installerKey">The key it joins with; one the test arranges in the database when none is given.</param>
  public static async Task<UiAgent> StartAsync(UiServer server, string machineName, string? installerKey = null) =>
    await ConnectAsync(server, machineName, installerKey ?? await ArrangeInstallerKeyAsync(server), identity: null);

  /// <summary>The same device again, as after its machine restarted: it signs with its own key and needs no installer key.</summary>
  public static Task<UiAgent> ReturnAsync(UiServer server, string machineName, AgentIdentity identity) =>
    ConnectAsync(server, machineName, installerKey: null, identity);

  private static async Task<UiAgent> ConnectAsync(UiServer server, string machineName, string? installerKey, AgentIdentity? identity)
  {

    var services = new ServiceCollection()
      .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
      .AddTransient<IHubConnectionBuilder>(_ => new HubConnectionBuilder().AddMessagePackProtocol())
      .AddRealtimeChannel<IAgentGateway, IAgentCallbacks, NoCallbacks>()
      .BuildServiceProvider();

    var channel = services.GetRequiredService<IRealtimeChannel<IAgentGateway>>();
    if (!await channel.OpenAsync(new Uri(server.BaseAddress, Routes.AgentGateway.TrimStart('/')), AcceptTestCertificates, TestContext.Current.CancellationToken))
    {
      throw new InvalidOperationException("The test agent could not reach the server.");
    }

    var agent = new UiAgent(services, channel, machineName, identity);
    var reply = await channel.Server.SubmitReport(agent._signer.Sign(agent.Report() with { InstallerKey = installerKey }, agent._keys.PrivateKey));
    if (!reply.Accepted)
    {
      await agent.DisposeAsync();
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

  // A deployment under test behind a reverse proxy has a certificate of its own making, which the browser accepts too.
  private static void AcceptTestCertificates(Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions options)
  {
    if (UiServer.Address is null)
    {
      return;
    }

    options.HttpMessageHandlerFactory = handler =>
    {
      if (handler is HttpClientHandler client)
      {
        client.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
      }

      return handler;
    };
    options.WebSocketConfiguration = socket => socket.RemoteCertificateValidationCallback = (_, _, _, _) => true;
  }

  // A persistent key of the server's only tenant, stored as the server stores one, for tests that aren't about keys.
  private static async Task<string> ArrangeInstallerKeyAsync(UiServer server)
  {
    var key = new InstallerKeyRecord { Id = Guid.NewGuid() };
    var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var hash = new PasswordHasher<InstallerKeyRecord>().HashPassword(key, secret);

    await using var connection = new NpgsqlConnection(server.Database);
    await connection.OpenAsync(TestContext.Current.CancellationToken);
    await using var command = new NpgsqlCommand(
      """
      INSERT INTO installer_keys ("Id", "TenantId", "Name", "Type", "CreatorKind", "CreatorId", "HashedSecret", "CreatedAt")
      SELECT @id, "Id", 'Test key', 'Persistent', 'User', @creator, @hash, now() FROM tenants LIMIT 1
      """,
      connection);
    command.Parameters.AddWithValue("id", key.Id);
    command.Parameters.AddWithValue("creator", Guid.NewGuid());
    command.Parameters.AddWithValue("hash", hash);
    if (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken) != 1)
    {
      throw new InvalidOperationException("There's no tenant for the test agent to join: register a user first.");
    }

    return $"{key.Id}:{secret}";
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

  private sealed class NoCallbacks : IAgentCallbacks
  {
    private const string NoAnswers = "This test agent has no answers.";

    public Task<GatewayReply<string>> GetAgentVersion(CancellationToken cancellationToken) =>
      Task.FromResult(GatewayReply.Refuse<string>(NoAnswers));

    public Task<GatewayReply<Guid>> OpenTerminal(Guid terminalId, CancellationToken cancellationToken) =>
      Task.FromResult(GatewayReply.Refuse<Guid>(NoAnswers));

    public Task<GatewayReply<StealthDesk.Contracts.Terminal.TerminalInputReceipt>> SendTerminalInput(Guid terminalId, string line, CancellationToken cancellationToken) =>
      Task.FromResult(GatewayReply.Refuse<StealthDesk.Contracts.Terminal.TerminalInputReceipt>(NoAnswers));

    public Task<GatewayReply<Guid>> CloseTerminal(Guid terminalId, CancellationToken cancellationToken) =>
      Task.FromResult(GatewayReply.Refuse<Guid>(NoAnswers));
  }
}

/// <summary>A device's id and private key, the identity an agent keeps between runs.</summary>
public sealed record AgentIdentity(Guid DeviceId, string PrivateKey);
