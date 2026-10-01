using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>End to end: a test agent talks to the real server, which stores into PostgreSQL.</summary>
public class GatewayTests
{
  [Fact]
  public async Task Negotiation_IsAvailable()
  {
    using var server = ServerHost.InMemory();
    using var client = server.CreateClient();

    var response = await client.PostAsync(
      $"{Routes.AgentGateway}/negotiate?negotiateVersion=1",
      content: null,
      TestContext.Current.CancellationToken);

    Assert.True(response.IsSuccessStatusCode);
  }

  [Fact]
  public async Task FirstReport_RegistersTheDeviceOnlineWithItsKey()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var agent = await TestAgent.ConnectAsync(server);
    var deviceId = Guid.NewGuid();

    var reply = await agent.ReportAsync(TestAgent.Report(deviceId));

    Assert.True(reply.Accepted, reply.Error);
    Assert.Equal(deviceId, reply.Value!.DeviceId);

    var device = await server.WithDbAsync(db => db.Devices.SingleAsync(x => x.Id == deviceId));
    Assert.True(device.IsOnline);
    Assert.Equal(agent.Keys.PublicKey, device.PublicKey);
    Assert.Equal("OFFICE-PC", device.Name);
    Assert.Equal(512, Assert.Single(device.Disks).SizeGb);
  }

  [Fact]
  public async Task FirstReport_WithoutAnId_GetsOneFromTheServer()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var agent = await TestAgent.ConnectAsync(server);

    var reply = await agent.ReportAsync(TestAgent.Report(Guid.Empty));

    Assert.True(reply.Accepted, reply.Error);
    Assert.NotEqual(Guid.Empty, reply.Value!.DeviceId);
    Assert.NotEqual(Guid.Empty, reply.Value.TenantId);
    Assert.True(await server.WithDbAsync(db => db.Devices.AnyAsync(x => x.Id == reply.Value.DeviceId)));
  }

  [Fact]
  public async Task LaterReports_WithTheSameKey_UpdateTheSameDevice()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var agent = await TestAgent.ConnectAsync(server);
    var deviceId = Guid.NewGuid();

    await agent.ReportAsync(TestAgent.Report(deviceId));
    var second = await agent.ReportAsync(TestAgent.Report(deviceId) with { CpuLoad = 0.9 });

    Assert.True(second.Accepted, second.Error);
    var devices = await server.WithDbAsync(db => db.Devices.ToListAsync());
    Assert.Equal(0.9, Assert.Single(devices).CpuLoad);
  }

  [Fact]
  public async Task Report_SignedWithAnotherKey_ForAKnownDevice_IsRefused()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var owner = await TestAgent.ConnectAsync(server);
    await using var impostor = await TestAgent.ConnectAsync(server);
    var deviceId = Guid.NewGuid();
    await owner.ReportAsync(TestAgent.Report(deviceId));

    var reply = await impostor.ReportAsync(TestAgent.Report(deviceId, "HIJACKED"));

    Assert.False(reply.Accepted);
    Assert.Equal("Signature verification failed.", reply.Error);
    var device = await server.WithDbAsync(db => db.Devices.SingleAsync(x => x.Id == deviceId));
    Assert.Equal(owner.Keys.PublicKey, device.PublicKey);
    Assert.Equal("OFFICE-PC", device.Name);
  }

  [Fact]
  public async Task Report_ChangedAfterSigning_IsRefused()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var agent = await TestAgent.ConnectAsync(server);
    var envelope = agent.Signer.Sign(TestAgent.Report(Guid.NewGuid()), agent.Keys.PrivateKey);

    var reply = await agent.SubmitAsync(envelope with { Payload = envelope.Payload with { MemoryTotalGb = 1024 } });

    Assert.False(reply.Accepted);
    Assert.Equal("Signature verification failed.", reply.Error);
    Assert.False(await server.WithDbAsync(db => db.Devices.AnyAsync()));
  }

  [Fact]
  public async Task Report_SignedOutsideTheClockTolerance_IsRefused()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var agent = await TestAgent.ConnectAsync(server);

    // Signed with a clock 5 minutes ahead; Development allows 1 minute.
    var aheadSigner = new StealthDesk.Core.Security.MessageSigner(new ShiftedClock(TimeSpan.FromMinutes(5)));
    var envelope = aheadSigner.Sign(TestAgent.Report(Guid.NewGuid()), agent.Keys.PrivateKey);

    var reply = await agent.SubmitAsync(envelope);

    Assert.False(reply.Accepted);
    Assert.Equal("Timestamp expired.", reply.Error);
  }

  [Fact]
  public async Task Report_FromADeviceTheServerForgot_RegistersItAgain()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var agent = await TestAgent.ConnectAsync(server);
    var deviceId = Guid.NewGuid();
    var defaultTenant = await server.WithDbAsync(db => db.Tenants.Select(x => x.Id).SingleAsync());

    // The ids an agent saved from a server whose database was later reset.
    var reply = await agent.ReportAsync(TestAgent.Report(deviceId) with { TenantId = Guid.NewGuid() });

    Assert.True(reply.Accepted, reply.Error);
    Assert.Equal(deviceId, reply.Value!.DeviceId);
    Assert.Equal(defaultTenant, reply.Value.TenantId);
    var device = await server.WithDbAsync(db => db.Devices.SingleAsync(x => x.Id == deviceId));
    Assert.Equal(agent.Keys.PublicKey, device.PublicKey);
  }

  [Fact]
  public async Task Report_FromADeviceTheServerForgot_IsRefusedWithoutSelfRegistration()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("Gateway:AllowSelfRegistration", "false");
    await using var agent = await TestAgent.ConnectAsync(server);

    var reply = await agent.ReportAsync(TestAgent.Report(Guid.NewGuid()) with { TenantId = Guid.NewGuid() });

    Assert.False(reply.Accepted);
    Assert.False(await server.WithDbAsync(db => db.Devices.AnyAsync()));
  }

  [Fact]
  public async Task ClosingTheConnection_MarksTheDeviceOffline()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var agent = await TestAgent.ConnectAsync(server);
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);

    await agent.DisposeAsync();

    var offline = await Eventually.TrueAsync(() =>
      server.WithDbAsync(db => db.Devices.AnyAsync(x => x.Id == deviceId && !x.IsOnline)));
    Assert.True(offline, "The device stayed online after its connection closed.");
  }

  [Fact]
  public async Task Reconnecting_WithTheSameKey_BringsTheSameDeviceBackOnline()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var deviceId = Guid.NewGuid();
    var first = await TestAgent.ConnectAsync(server);
    await first.ReportAsync(TestAgent.Report(deviceId));
    await first.DisposeAsync();
    await Eventually.TrueAsync(() => server.WithDbAsync(db => db.Devices.AnyAsync(x => !x.IsOnline)));

    await using var second = await TestAgent.ConnectAsync(server, first.Keys);
    var reply = await second.ReportAsync(TestAgent.Report(deviceId));

    Assert.True(reply.Accepted, reply.Error);
    var device = Assert.Single(await server.WithDbAsync(db => db.Devices.ToListAsync()));
    Assert.True(device.IsOnline);
  }

  private sealed class ShiftedClock(TimeSpan shift) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + shift;
  }
}
