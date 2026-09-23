using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Libraries.Api.Contracts.Dtos.ServerApi.V1;
using StealthDesk.Libraries.Api.Contracts.Enums;
using StealthDesk.Web.Server.Data;
using StealthDesk.Web.Server.Data.Entities;

namespace StealthDesk.Web.Server.Tests;

public class DevicesControllerTests
{
  [Fact]
  public async Task Get_WhenDeviceExists_ReturnsIt()
  {
    using var factory = new TestAppFactory();
    using var client = factory.CreateClient();

    using (var scope = factory.Services.CreateScope())
    {
      var appDb = scope.ServiceProvider.GetRequiredService<AppDb>();
      var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Test Tenant" };
      appDb.Tenants.Add(tenant);
      appDb.Devices.Add(new Device
      {
        Id = Guid.NewGuid(),
        TenantId = tenant.Id,
        Name = "TEST-PC",
        AgentVersion = "1.0.0",
        Platform = SystemPlatform.Windows,
        IsOnline = true
      });
      await appDb.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    var response = await client.GetAsync("/api/v1/devices", TestContext.Current.CancellationToken);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var devices = await response.Content.ReadFromJsonAsync<List<DeviceResponseDto>>(
      TestContext.Current.CancellationToken);

    Assert.NotNull(devices);
    var device = Assert.Single(devices);
    Assert.Equal("TEST-PC", device.Name);
    Assert.Equal(SystemPlatform.Windows, device.Platform);
    Assert.True(device.IsOnline);
  }

  [Fact]
  public async Task Get_WhenNoDevicesExist_ReturnsEmptyList()
  {
    using var factory = new TestAppFactory();
    using var client = factory.CreateClient();

    var response = await client.GetAsync("/api/v1/devices", TestContext.Current.CancellationToken);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var devices = await response.Content.ReadFromJsonAsync<List<DeviceResponseDto>>(
      TestContext.Current.CancellationToken);

    Assert.NotNull(devices);
    Assert.Empty(devices);
  }
}
