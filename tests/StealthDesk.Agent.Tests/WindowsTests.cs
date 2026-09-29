using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StealthDesk.Agent.Core;
using StealthDesk.Agent.Core.Inventory;
using StealthDesk.Agent.Windows;

namespace StealthDesk.Agent.Tests;

/// <summary>Tests that need the real Windows APIs. Skipped on other systems.</summary>
public class WindowsTests
{
  [Fact]
  [SupportedOSPlatform("windows6.1")]
  public async Task Inventory_DescribesThisMachine()
  {
    Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(6, 1), "Needs Windows.");

    var inventory = new WindowsInventory(new FixedLoad(0.3));

    var report = await inventory.CaptureAsync(TestContext.Current.CancellationToken);

    Assert.False(string.IsNullOrWhiteSpace(report.MachineName));
    Assert.Equal(DevicePlatform.Windows, report.Platform);
    Assert.True(report.CpuCores > 0);
    Assert.True(report.MemoryTotalGb > 0);
    Assert.InRange(report.MemoryUsedGb, 0.01, report.MemoryTotalGb);
    Assert.NotEmpty(report.Disks);
    Assert.Equal(0.3, report.CpuLoad);
    Assert.Equal(Guid.Empty, report.DeviceId);
  }

  [Fact]
  [SupportedOSPlatform("windows6.1")]
  public void AgentServices_AllResolve()
  {
    Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(6, 1), "Needs Windows.");

    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
    builder.AddStealthDeskAgent(new AgentStartup("tests", new Uri("http://localhost:5099"), UseSettingsFile: false));
    builder.Services.AddWindowsInventory();
    var registrations = builder.Services.Where(x => !x.ServiceType.IsGenericTypeDefinition).ToList();

    // Development validates the whole graph on Build; resolving each one also catches factory mistakes.
    using var host = builder.Build();
    using var scope = host.Services.CreateScope();
    foreach (var registration in registrations)
    {
      Assert.NotNull(scope.ServiceProvider.GetService(registration.ServiceType));
    }
  }

  [Theory]
  [InlineData(100, 1000, 90, 900, 0)]      // nothing but idle time passed
  [InlineData(100, 1000, 100, 1100, 1)]    // no idle time at all
  [InlineData(100, 1000, 150, 1100, 0.5)]  // half of the time idle
  [InlineData(100, 1000, 100, 1000, 0)]    // no time passed
  public void CpuLoad_IsTheBusyShareOfTheTimeBetweenSamples(ulong idleBefore, ulong totalBefore, ulong idleAfter, ulong totalAfter, double expected)
  {
    Assert.Equal(expected, CpuTimes.BusyShare(new(idleBefore, totalBefore), new(idleAfter, totalAfter)), precision: 3);
  }

  private sealed class FixedLoad(double value) : ICpuLoad
  {
    public double Current => value;
  }
}
