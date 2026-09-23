using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Libraries.Api.Contracts.Dtos.Devices;
using StealthDesk.Libraries.Api.Contracts.Dtos.HubDtos;
using StealthDesk.Libraries.Api.Contracts.Enums;
using StealthDesk.Web.Server.Data;
using StealthDesk.Web.Server.Data.Entities;
using StealthDesk.Web.Server.Services.DeviceManagement;
using StealthDesk.Web.Server.Tests.Helpers;

namespace StealthDesk.Web.Server.Tests;

public class DeviceManagerTests
{
  [Fact]
  [SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "Because")]
  public async Task DeviceManager_AddOrUpdate()
  {
    // Arrange
    using var factory = new TestAppFactory();
    using var scope = factory.Services.CreateScope();
    var deviceManager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();

    var deviceId = Guid.NewGuid();
    var tenant = await factory.Services.CreateTestTenant();
    var tenantId = tenant.Id;

    var deviceDto = new DeviceUpdateRequestDto(
      Name: "Test Device",
      AgentVersion: "1.0.0",
      CpuUtilization: 50,
      Id: deviceId,
      Is64Bit: true,
      OsArchitecture: Architecture.X64,
      Platform: SystemPlatform.Windows,
      ProcessorCount: 8,
      OsDescription: "Windows 10",
      TenantId: tenantId,
      TotalMemory: 16384,
      TotalStorage: 1024000,
      UsedMemory: 8192,
      UsedStorage: 512000,
      CurrentUsers: ["User1", "User2"],
      MacAddresses: ["00:00:00:00:00:01"],
      LocalIpV4: "10.0.0.1",
      LocalIpV6: "fe80::1",
      Drives: [new Drive { Name = "C:", VolumeLabel = "Local Disk", TotalSize = 1024000, FreeSpace = 512000 }],
      DnsHostName: "test-device.contoso.local");

    var connectionContext = new DeviceConnectionContext(
      ConnectionId: "test-connection-id",
      RemoteIpAddress: IPAddress.Parse("127.0.0.1"),
      LastSeen: DateTimeOffset.Now,
      IsOnline: true);

    // Act
    var result = await deviceManager.AddOrUpdate(deviceDto, connectionContext);

    // Assert
    Assert.NotNull(result);
    Assert.Equal(deviceId, result.Id);
    Assert.Equal("Test Device", result.Name);
    Assert.Equal("1.0.0", result.AgentVersion);
    Assert.Equal(50, result.CpuUtilization);
    Assert.True(result.Is64Bit);
    Assert.True(result.IsOnline);
    Assert.Equal(Architecture.X64, result.OsArchitecture);
    Assert.Equal(SystemPlatform.Windows, result.Platform);
    Assert.Equal(8, result.ProcessorCount);
    Assert.Equal("test-connection-id", result.ConnectionId);
    Assert.Equal("Windows 10", result.OsDescription);
    Assert.Equal(tenantId, result.TenantId);
    Assert.Equal(16384, result.TotalMemory);
    Assert.Equal(1024000, result.TotalStorage);
    Assert.Equal(8192, result.UsedMemory);
    Assert.Equal(512000, result.UsedStorage);
    Assert.Equal(new[] { "User1", "User2" }, result.CurrentUsers);
    Assert.Equal(new[] { "00:00:00:00:00:01" }, result.MacAddresses);
    Assert.Equal("test-device.contoso.local", result.DnsHostName);
    Assert.Equal("127.0.0.1", result.PublicIpV4);
    Assert.Equal(string.Empty, result.PublicIpV6);
    Assert.Single(result.Drives);
    Assert.Equal("C:", result.Drives[0].Name);

    // Verify Alias isn't updated (DeviceDto.Alias shouldn't update the entity)
    Assert.Equal(string.Empty, result.Alias);

    // Test update of existing device
    var updatedDto = deviceDto with
    {
      Name = "Updated Device",
      AgentVersion = "1.0.1",
      OsDescription = "Windows 11"
    };

    var updatedResult = await deviceManager.AddOrUpdate(updatedDto, connectionContext);

    Assert.NotNull(updatedResult);
    Assert.Equal(deviceId, updatedResult.Id);
    Assert.Equal("Updated Device", updatedResult.Name);
    Assert.Equal("1.0.1", updatedResult.AgentVersion);
    Assert.Equal("Windows 11", updatedResult.OsDescription);
  }

  [Fact]
  public async Task DeviceManager_UpdateDevice()
  {
    // Arrange
    using var factory = new TestAppFactory();
    using var scope = factory.Services.CreateScope();
    var deviceManager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();

    var deviceId = Guid.NewGuid();
    var tenant = await factory.Services.CreateTestTenant();
    var tenantId = tenant.Id;

    // Create a device in the database first
    var device = new Device
    {
      Id = deviceId,
      Name = "Original Device",
      AgentVersion = "1.0.0",
      TenantId = tenantId,
      Alias = "Original Alias"
    };
    db.Devices.Add(device);
    await db.SaveChangesAsync(TestContext.Current.CancellationToken);

    var deviceDto = new DeviceUpdateRequestDto(
      Name: "Updated Device",
      AgentVersion: "2.0.0",
      CpuUtilization: 75,
      Id: deviceId,
      Is64Bit: true,
      OsArchitecture: Architecture.X64,
      Platform: SystemPlatform.Windows,
      ProcessorCount: 8,
      OsDescription: "Windows 11",
      TenantId: tenantId,
      TotalMemory: 32768,
      TotalStorage: 2048000,
      UsedMemory: 16384,
      UsedStorage: 1024000,
      CurrentUsers: ["User1"],
      MacAddresses: ["00:00:00:00:00:02"],
      LocalIpV4: "192.168.0.1",
      LocalIpV6: "fe80::1",
      Drives: [new Drive { Name = "C:", VolumeLabel = "System", TotalSize = 2048000, FreeSpace = 1024000 }],
      DnsHostName: "updated-device.contoso.local");

    var connectionContext = new DeviceConnectionContext(
      ConnectionId: "test-connection-id",
      RemoteIpAddress: IPAddress.Parse("192.168.1.1"),
      LastSeen: DateTimeOffset.Now,
      IsOnline: true);

    // Act
    var result = await deviceManager.UpdateDevice(deviceDto, connectionContext);

    // Assert
    Assert.True(result.IsSuccess);
    var updatedDevice = result.Value;

    Assert.NotNull(updatedDevice);
    Assert.Equal(deviceId, updatedDevice.Id);
    Assert.Equal("Updated Device", updatedDevice.Name);
    Assert.Equal("2.0.0", updatedDevice.AgentVersion);
    Assert.Equal(75, updatedDevice.CpuUtilization);
    Assert.True(updatedDevice.Is64Bit);
    Assert.True(updatedDevice.IsOnline);
    Assert.Equal(Architecture.X64, updatedDevice.OsArchitecture);
    Assert.Equal(SystemPlatform.Windows, updatedDevice.Platform);
    Assert.Equal(8, updatedDevice.ProcessorCount);
    Assert.Equal("test-connection-id", updatedDevice.ConnectionId);
    Assert.Equal("Windows 11", updatedDevice.OsDescription);
    Assert.Equal(tenantId, updatedDevice.TenantId);
    Assert.Equal("updated-device.contoso.local", updatedDevice.DnsHostName);

    // Verify Alias isn't updated (DeviceDto.Alias shouldn't update the entity)
    Assert.Equal("Original Alias", updatedDevice.Alias);

    // Test update with non-existent device ID
    var nonExistentDto = deviceDto with { Id = Guid.NewGuid() };
    var failResult = await deviceManager.UpdateDevice(nonExistentDto, connectionContext);
    Assert.False(failResult.IsSuccess);
    Assert.Equal("Device does not exist in the database.", failResult.Reason);
  }

  [Fact]
  public async Task DeviceManager_UpdateDevice_RejectsCrossTenantMove()
  {
    using var factory = new TestAppFactory();
    using var scope = factory.Services.CreateScope();
    var deviceManager = scope.ServiceProvider.GetRequiredService<IDeviceManager>();
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();

    var deviceId = Guid.NewGuid();
    var tenantA = await factory.Services.CreateTestTenant();
    var tenantB = await factory.Services.CreateTestTenant();

    db.Devices.Add(new Device
    {
      Id = deviceId,
      Name = "Original Device",
      AgentVersion = "1.0.0",
      TenantId = tenantA.Id,
    });
    await db.SaveChangesAsync(TestContext.Current.CancellationToken);

    var deviceDto = new DeviceUpdateRequestDto(
      Name: "Updated Device",
      AgentVersion: "2.0.0",
      CpuUtilization: 75,
      Id: deviceId,
      Is64Bit: true,
      OsArchitecture: Architecture.X64,
      Platform: SystemPlatform.Windows,
      ProcessorCount: 8,
      OsDescription: "Windows 11",
      TenantId: tenantB.Id,
      TotalMemory: 32768,
      TotalStorage: 2048000,
      UsedMemory: 16384,
      UsedStorage: 1024000,
      CurrentUsers: ["User1"],
      MacAddresses: ["00:00:00:00:00:02"],
      LocalIpV4: "192.168.0.1",
      LocalIpV6: "fe80::1",
      Drives: [new Drive { Name = "C:", VolumeLabel = "System", TotalSize = 2048000, FreeSpace = 1024000 }],
      DnsHostName: "updated-device.contoso.local");

    var connectionContext = new DeviceConnectionContext(
      ConnectionId: "test-connection-id",
      RemoteIpAddress: IPAddress.Parse("192.168.1.1"),
      LastSeen: DateTimeOffset.Now,
      IsOnline: true);

    var result = await deviceManager.UpdateDevice(deviceDto, connectionContext);

    Assert.False(result.IsSuccess);

    var dbDevice = await db.Devices
      .IgnoreQueryFilters()
      .FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken: TestContext.Current.CancellationToken);
    Assert.NotNull(dbDevice);
    Assert.Equal(tenantA.Id, dbDevice.TenantId);
  }
}
