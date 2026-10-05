using System.Net;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Devices;

namespace StealthDesk.Server.Tests;

/// <summary>The storage rules, straight against PostgreSQL.</summary>
public class DeviceRegistryTests
{
  private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

  [Fact]
  public async Task SaveReport_CreatesAnUnknownDevice_OnlyWhenAllowed()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var tenantId = await DefaultTenant(server);
    var report = TestAgent.Report(Guid.NewGuid()) with { TenantId = tenantId };

    var refused = await Save(server, report, allowNew: false);
    var created = await Save(server, report, allowNew: true);

    Assert.False(refused.Succeeded);
    Assert.Equal("Unknown device.", refused.Error);
    Assert.True(created.Succeeded, created.Error);
    Assert.Equal(report.DeviceId, created.Value!.Id);
  }

  [Fact]
  public async Task SaveReport_RecordsTheConnectionAndTheAddressTheServerSaw()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var report = TestAgent.Report(Guid.NewGuid()) with { TenantId = await DefaultTenant(server) };

    await Save(server, report, allowNew: true, connectionId: "connection-1", remote: IPAddress.Parse("203.0.113.7"));

    var device = await server.WithDbAsync(db => db.Devices.SingleAsync());
    Assert.Equal("connection-1", device.ConnectionId);
    Assert.Equal("203.0.113.7", device.PublicIpV4);
    Assert.True(device.IsOnline);
  }

  [Fact]
  public async Task SaveReport_RefusesToMoveADeviceToAnotherTenant()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var original = await DefaultTenant(server);
    var other = await server.WithDbAsync(async db =>
    {
      var tenant = new TenantRecord { Name = "Other" };
      db.Tenants.Add(tenant);
      await db.SaveChangesAsync();
      return tenant.Id;
    });

    var report = TestAgent.Report(Guid.NewGuid()) with { TenantId = original };
    await Save(server, report, allowNew: true);

    var moved = await Save(server, report with { TenantId = other }, allowNew: true);

    Assert.False(moved.Succeeded);
    Assert.Equal(original, await server.WithDbAsync(db => db.Devices.Select(x => x.TenantId).SingleAsync()));
  }

  [Fact]
  public async Task SaveReport_CutsOversizedTextInsteadOfFailing()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var report = TestAgent.Report(Guid.NewGuid()) with
    {
      TenantId = await DefaultTenant(server),
      MachineName = new string('x', 500),
    };

    var saved = await Save(server, report, allowNew: true);

    Assert.True(saved.Succeeded, saved.Error);
    Assert.Equal(DeviceRecord.NameMax, saved.Value!.Name.Length);
  }

  [Fact]
  public async Task MarkOffline_OnlyAppliesToTheCurrentConnection()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var report = TestAgent.Report(Guid.NewGuid()) with { TenantId = await DefaultTenant(server) };
    await Save(server, report, allowNew: true, connectionId: "old");
    await Save(server, report, allowNew: true, connectionId: "new");

    var staleClosed = await Registry(server, registry => registry.MarkOfflineAsync(report.DeviceId, "old", DateTimeOffset.UtcNow));
    var currentClosed = await Registry(server, registry => registry.MarkOfflineAsync(report.DeviceId, "new", DateTimeOffset.UtcNow));

    Assert.Null(staleClosed);
    Assert.NotNull(currentClosed);
    Assert.False(currentClosed.IsOnline);
    Assert.False(await server.WithDbAsync(db => db.Devices.Select(x => x.IsOnline).SingleAsync()));
  }

  private static Task<Guid> DefaultTenant(ServerHost server) =>
    TestTenants.EnsureAsync(server);

  private static Task<StealthDesk.Core.Outcome<DeviceRecord>> Save(
    ServerHost server,
    DeviceReport report,
    bool allowNew,
    string connectionId = "connection",
    IPAddress? remote = null)
  {
    return Registry(server, registry => registry.SaveReportAsync(
      report,
      new ReportOrigin(connectionId, remote, DateTimeOffset.UtcNow),
      Key,
      allowNew));
  }

  private static async Task<T> Registry<T>(ServerHost server, Func<IDeviceRegistry, Task<T>> action)
  {
    await using var scope = server.Services.CreateAsyncScope();
    return await action(scope.ServiceProvider.GetRequiredService<IDeviceRegistry>());
  }
}
