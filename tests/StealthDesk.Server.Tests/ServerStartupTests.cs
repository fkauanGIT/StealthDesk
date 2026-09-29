using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using StealthDesk.Branding;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Gateway;

namespace StealthDesk.Server.Tests;

public class ServerStartupTests
{
  [Fact]
  public async Task EmptyDatabase_GetsTheDefaultTenant()
  {
    using var server = ServerHost.InMemory();

    var tenants = await server.WithDbAsync(db => db.Tenants.ToListAsync());

    Assert.Equal(DatabaseSetup.DefaultTenantName, Assert.Single(tenants).Name);
  }

  [Fact]
  public async Task Restart_DoesNotAddASecondTenant()
  {
    var database = Guid.NewGuid().ToString("N");
    using (var firstRun = ServerHost.InMemory(database))
    {
      Assert.Single(await firstRun.WithDbAsync(db => db.Tenants.ToListAsync()));
    }

    using var secondRun = ServerHost.InMemory(database);
    Assert.Single(await secondRun.WithDbAsync(db => db.Tenants.ToListAsync()));
  }

  [Fact]
  public async Task Migrations_BuildTheSchemaOnAnEmptyPostgresDatabase()
  {
    using var server = await ServerHost.OnPostgresAsync();

    var pending = await server.WithDbAsync(db => db.Database.GetPendingMigrationsAsync());

    Assert.Empty(pending);
    Assert.Single(await server.WithDbAsync(db => db.Tenants.ToListAsync()));
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
  public void ServerAssemblyName_MatchesTheRealAssembly()
  {
    Assert.Equal(Brand.ServerAssemblyName, typeof(Program).Assembly.GetName().Name);
  }
}
