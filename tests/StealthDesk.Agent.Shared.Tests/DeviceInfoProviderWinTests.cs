using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StealthDesk.Agent.Shared.Services;
using StealthDesk.Agent.Shared.Services.Windows;
using StealthDesk.Libraries.Api.Contracts.Enums;
using StealthDesk.Libraries.NativeInterop.Windows;
using StealthDesk.Libraries.Shared.Services;
using StealthDesk.Libraries.Shared.Services.FileSystem;

namespace StealthDesk.Agent.Shared.Tests;

public class DeviceInfoProviderWinTests
{
  [Fact]
  [SupportedOSPlatform("windows8.0")]
  public async Task GetDeviceInfo_ReturnsCurrentMachineInfo()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Reads device information through Win32 APIs.");

    var deviceId = Guid.NewGuid();
    var tenantId = Guid.NewGuid();
    var optionsAccessor = new Mock<IOptionsAccessor>();
    optionsAccessor.SetupGet(x => x.DeviceId).Returns(deviceId);
    optionsAccessor.SetupGet(x => x.TenantId).Returns(tenantId);

    var provider = new DeviceInfoProviderWin(
      new Win32Interop(NullLogger<Win32Interop>.Instance),
      new FileSystem(NullLogger<FileSystem>.Instance),
      SystemEnvironment.Instance,
      new CpuUtilizationSampler(ElevationCheckerWin.Instance, NullLogger<CpuUtilizationSampler>.Instance),
      optionsAccessor.Object,
      NullLogger<DeviceInfoProviderWin>.Instance);

    var deviceInfo = await provider.GetDeviceInfo();

    Assert.Equal(deviceId, deviceInfo.Id);
    Assert.Equal(tenantId, deviceInfo.TenantId);
    Assert.False(string.IsNullOrWhiteSpace(deviceInfo.Name));
    Assert.True(deviceInfo.ProcessorCount > 0);
    Assert.True(deviceInfo.TotalMemory > 0);
    Assert.True(deviceInfo.UsedMemory > 0);
    Assert.Equal(SystemPlatform.Windows, deviceInfo.Platform);
    Assert.NotEmpty(deviceInfo.Drives);
  }
}
