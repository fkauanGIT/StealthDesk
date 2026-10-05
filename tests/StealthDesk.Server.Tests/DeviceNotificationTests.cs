using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Dashboard;

namespace StealthDesk.Server.Tests;

/// <summary>
/// End to end: what the dashboards hear when agents report and disconnect, with the devices stored in PostgreSQL.
/// </summary>
public class DeviceNotificationTests
{
  [Fact]
  public async Task AcceptedReport_ReachesDashboardsAsOnline()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var dashboard = await TestDashboard.ConnectSignedInAsync(server);
    await using var agent = await TestAgent.ConnectAsync(server);
    var deviceId = Guid.NewGuid();

    await agent.ReportAsync(TestAgent.Report(deviceId));

    var device = await dashboard.NextChangeAsync();
    Assert.Equal(deviceId, device.Id);
    Assert.Equal("OFFICE-PC", device.Name);
    Assert.True(device.IsOnline);
    Assert.Equal(["alice"], device.LoggedOnUsers);
    Assert.Equal(["00155D012345"], device.MacAddresses);
    var disk = Assert.Single(device.Disks);
    Assert.Equal("NTFS", disk.Format);
    Assert.Equal(302, disk.FreeGb);
  }

  [Fact]
  public async Task AgentDisconnecting_ReachesDashboardsAsOffline()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var dashboard = await TestDashboard.ConnectSignedInAsync(server);
    var agent = await TestAgent.ConnectAsync(server);
    var deviceId = Guid.NewGuid();
    await agent.ReportAsync(TestAgent.Report(deviceId));
    await dashboard.NextChangeAsync();

    await agent.DisposeAsync();

    var device = await dashboard.NextChangeAsync();
    Assert.Equal(deviceId, device.Id);
    Assert.False(device.IsOnline);
  }

  [Fact]
  public async Task RefusedReport_IsNotSentToDashboards()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var dashboard = await TestDashboard.ConnectSignedInAsync(server);
    await using var agent = await TestAgent.ConnectAsync(server);
    var envelope = agent.Signer.Sign(TestAgent.Report(Guid.NewGuid()), agent.Keys.PrivateKey);

    var reply = await agent.SubmitAsync(envelope with { Payload = envelope.Payload with { CpuCores = 64 } });

    Assert.False(reply.Accepted);
    await Assert.ThrowsAsync<TimeoutException>(() => dashboard.NextChangeAsync(within: TimeSpan.FromSeconds(1)));
  }

  [Fact]
  public async Task FailingToNotify_IsLoggedAndNotThrown()
  {
    var logger = new RecordingLogger();
    var notifier = new DeviceNotifier(new BrokenHubContext(), logger);

    await notifier.DeviceChangedAsync(new DeviceRecord { Id = Guid.NewGuid() });

    Assert.Equal(LogLevel.Warning, Assert.Single(logger.Levels));
  }

  private sealed class RecordingLogger : ILogger<DeviceNotifier>
  {
    public List<LogLevel> Levels { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Levels.Add(logLevel);
  }

  // Every send fails, like a dashboard connection dropping mid-write.
  private sealed class BrokenHubContext : IHubContext<DashboardHub, IDashboardCallbacks>, IHubClients<IDashboardCallbacks>, IDashboardCallbacks
  {
    public IHubClients<IDashboardCallbacks> Clients => this;
    public IGroupManager Groups => throw new NotSupportedException();

    IDashboardCallbacks IHubClients<IDashboardCallbacks>.All => this;
    IDashboardCallbacks IHubClients<IDashboardCallbacks>.AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
    IDashboardCallbacks IHubClients<IDashboardCallbacks>.Client(string connectionId) => this;
    IDashboardCallbacks IHubClients<IDashboardCallbacks>.Clients(IReadOnlyList<string> connectionIds) => this;
    IDashboardCallbacks IHubClients<IDashboardCallbacks>.Group(string groupName) => this;
    IDashboardCallbacks IHubClients<IDashboardCallbacks>.GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;
    IDashboardCallbacks IHubClients<IDashboardCallbacks>.Groups(IReadOnlyList<string> groupNames) => this;
    IDashboardCallbacks IHubClients<IDashboardCallbacks>.User(string userId) => this;
    IDashboardCallbacks IHubClients<IDashboardCallbacks>.Users(IReadOnlyList<string> userIds) => this;

    public Task DeviceChanged(DeviceSummary device) => throw new IOException("Connection lost.");
  }
}
