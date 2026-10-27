using System.Net;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Accounts;
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

  // The old connection closing lands between the new connection's report reading the device and saving it.
  [Fact]
  public async Task SaveReport_KeepsTheDeviceOnline_WhenTheOldConnectionClosesWhileItSaves()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var report = TestAgent.Report(Guid.NewGuid()) with { TenantId = await DefaultTenant(server) };
    await Save(server, report, allowNew: true, connectionId: "old");

    await using var scope = server.Services.CreateAsyncScope();
    var options = new DbContextOptionsBuilder<StealthDeskDb>(scope.ServiceProvider.GetRequiredService<DbContextOptions<StealthDeskDb>>())
      .AddInterceptors(new BeforeSaving(() => Registry(server, registry => registry.MarkOfflineAsync(report.DeviceId, "old", DateTimeOffset.UtcNow))))
      .Options;
    await using var db = new StealthDeskDb(options, scope.ServiceProvider.GetRequiredService<ITenantScope>());
    var saved = await new DeviceRegistry(db, NullLogger<DeviceRegistry>.Instance)
      .SaveReportAsync(report, new ReportOrigin("new", null, DateTimeOffset.UtcNow), Key, allowNew: true, TestContext.Current.CancellationToken);

    Assert.True(saved.Succeeded, saved.Error);
    var device = await server.WithDbAsync(x => x.Devices.SingleAsync());
    Assert.Equal("new", device.ConnectionId);
    Assert.True(device.IsOnline);
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

  // Runs something once, as the first save starts: after the device was read, before anything is written.
  private sealed class BeforeSaving(Func<Task> action) : SaveChangesInterceptor
  {
    private bool _done;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
      DbContextEventData eventData,
      InterceptionResult<int> result,
      CancellationToken cancellationToken = default)
    {
      if (!_done)
      {
        _done = true;
        await action();
      }

      return result;
    }
  }

  private static async Task<T> Registry<T>(ServerHost server, Func<IDeviceRegistry, Task<T>> action)
  {
    await using var scope = server.Services.CreateAsyncScope();
    return await action(scope.ServiceProvider.GetRequiredService<IDeviceRegistry>());
  }
}
