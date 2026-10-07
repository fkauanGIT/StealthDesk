using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using StealthDesk.Branding;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Gateway;

namespace StealthDesk.Server.Tests;

public class ServerStartupTests
{
  [Fact]
  public async Task NewServer_StartsWithoutTenantsOrUsers()
  {
    using var server = ServerHost.InMemory();

    Assert.False(await server.WithDbAsync(db => db.Tenants.AnyAsync()));
    Assert.False(await server.WithDbAsync(db => db.Users.AnyAsync()));
  }

  [Fact]
  public async Task Restart_KeepsTheTenantsThereAre()
  {
    var database = Guid.NewGuid().ToString("N");
    using (var firstRun = ServerHost.InMemory(database))
    {
      await TestTenants.CreateAsync(firstRun);
    }

    using var secondRun = ServerHost.InMemory(database);
    Assert.Single(await secondRun.WithDbAsync(db => db.Tenants.ToListAsync()));
  }

  [Fact]
  public async Task Restart_MarksDevicesStoredOnlineOffline_InMemory()
  {
    using var firstRun = ServerHost.InMemory();
    await AssertRestartMarksDevicesOfflineAsync(firstRun);
  }

  [Fact]
  public async Task Restart_MarksDevicesStoredOnlineOffline_OnPostgres()
  {
    using var firstRun = await ServerHost.OnPostgresAsync();
    await AssertRestartMarksDevicesOfflineAsync(firstRun);
  }

  [Fact]
  public async Task Migrations_BuildTheSchemaOnAnEmptyPostgresDatabase()
  {
    using var server = await ServerHost.OnPostgresAsync();

    var pending = await server.WithDbAsync(db => db.Database.GetPendingMigrationsAsync());

    Assert.Empty(pending);
    Assert.False(await server.WithDbAsync(db => db.Tenants.AnyAsync()));
  }

  [Fact]
  public void DevelopmentSettings_AllowSelfRegistrationWithAOneMinuteClockTolerance()
  {
    using var server = ServerHost.InMemory();

    var options = server.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;

    Assert.True(options.AllowSelfRegistration);
    Assert.Equal(TimeSpan.FromMinutes(1), options.ClockTolerance);
  }

  [Fact]
  public void MissingGatewaySection_TurnsBothRulesOff()
  {
    var options = new ConfigurationBuilder().Build()
      .GetSection(GatewayOptions.Section)
      .Get<GatewayOptions>() ?? new GatewayOptions();

    Assert.Null(options.ClockTolerance);
    Assert.False(options.AllowSelfRegistration);
  }

  [Fact]
  public async Task VersionEndpoint_ReportsTheProductVersion()
  {
    using var server = ServerHost.InMemory();
    using var client = server.CreateClient();

    var version = await client.GetStringAsync(Routes.ServerVersion, TestContext.Current.CancellationToken);

    // Update together with <Version> in Directory.Build.props when a release is cut.
    Assert.Equal("0.3.0.0", version);
  }

  [Fact]
  public void ServerAssemblyName_MatchesTheRealAssembly()
  {
    Assert.Equal(Brand.ServerAssemblyName, typeof(Program).Assembly.GetName().Name);
  }

  // The first server stays up while the second starts, so its agents never get to close their connections:
  // like a server that stopped abruptly.
  private static async Task AssertRestartMarksDevicesOfflineAsync(ServerHost firstRun)
  {
    var deviceId = Guid.NewGuid();
    await firstRun.WithDbAsync(async db =>
    {
      var tenant = new TenantRecord { Name = TestTenants.Name };
      db.Devices.Add(new DeviceRecord { Id = deviceId, Tenant = tenant, IsOnline = true, ConnectionId = "lost" });
      return await db.SaveChangesAsync();
    });

    using var secondRun = firstRun.Restarted();

    var device = await secondRun.WithDbAsync(db => db.Devices.SingleAsync(x => x.Id == deviceId));
    Assert.False(device.IsOnline);
    Assert.Empty(device.ConnectionId);
  }
}
