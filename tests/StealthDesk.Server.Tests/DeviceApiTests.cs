using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

public class DeviceApiTests
{
  [Fact]
  public async Task Devices_IsEmptyWhenNothingReported()
  {
    using var server = ServerHost.InMemory();
    using var client = server.CreateClient();

    var devices = await client.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, TestContext.Current.CancellationToken);

    Assert.NotNull(devices);
    Assert.Empty(devices);
  }

  [Fact]
  public async Task Devices_ListsStoredDevices()
  {
    using var server = ServerHost.InMemory();
    using var client = server.CreateClient();
    await server.WithDbAsync(async db =>
    {
      var tenant = await db.Tenants.SingleAsync();
      db.Devices.Add(new DeviceRecord
      {
        Id = Guid.NewGuid(),
        TenantId = tenant.Id,
        Name = "FRONT-DESK",
        Platform = DevicePlatform.Windows,
        IsOnline = true,
        Disks = [new DiskInfo { Name = @"C:\", SizeGb = 256 }],
      });
      return await db.SaveChangesAsync();
    });

    var devices = await client.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, TestContext.Current.CancellationToken);

    var device = Assert.Single(devices!);
    Assert.Equal("FRONT-DESK", device.Name);
    Assert.Equal(DevicePlatform.Windows, device.Platform);
    Assert.True(device.IsOnline);
    Assert.Equal(256, Assert.Single(device.Disks).SizeGb);
  }
}
