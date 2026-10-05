using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

public class DeviceApiTests
{
  [Fact]
  public async Task Devices_IsEmptyWhenNothingReported()
  {
    using var server = ServerHost.InMemory();
    using var client = await TestAccounts.SignedInClientAsync(server);

    var devices = await client.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, TestContext.Current.CancellationToken);

    Assert.NotNull(devices);
    Assert.Empty(devices);
  }

  [Fact]
  public async Task Devices_ListsStoredDevices()
  {
    using var server = ServerHost.InMemory();
    using var client = await TestAccounts.SignedInClientAsync(server);
    var tenantId = await TestTenants.EnsureAsync(server);
    await server.WithDbAsync(async db =>
    {
      db.Devices.Add(new DeviceRecord
      {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
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

  [Fact]
  public async Task Device_ReturnsTheAgentsLastReport()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var agent = await TestAgent.ConnectAsync(server);
    var deviceId = Guid.NewGuid();
    await agent.ReportAsync(TestAgent.Report(deviceId));
    using var client = await TestAccounts.SignedInClientAsync(server);

    var device = await client.GetFromJsonAsync<DeviceSummary>(Routes.Device(deviceId), TestContext.Current.CancellationToken);

    var report = TestAgent.Report(deviceId);
    Assert.Equal(report.MachineName, device!.Name);
    Assert.Equal(report.DnsName, device.DnsName);
    Assert.Equal(report.AgentVersion, device.AgentVersion);
    Assert.Equal(report.LocalIpV4, device.LocalIpV4);
    Assert.Equal(report.MacAddresses, device.MacAddresses);
    Assert.Equal(report.Disks, device.Disks);
    Assert.True(device.IsOnline);
  }

  [Fact]
  public async Task Device_UnknownId_IsNotFound()
  {
    using var server = ServerHost.InMemory();
    using var client = await TestAccounts.SignedInClientAsync(server);

    var response = await client.GetAsync(Routes.Device(Guid.NewGuid()), TestContext.Current.CancellationToken);

    Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
  }
}
