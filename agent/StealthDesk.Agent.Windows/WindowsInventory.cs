using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StealthDesk.Agent.Core.Inventory;
using StealthDesk.Contracts.Devices;
using Windows.Win32.System.SystemInformation;

namespace StealthDesk.Agent.Windows;

[SupportedOSPlatform("windows6.1")]
public sealed class WindowsInventory(ICpuLoad cpu) : IDeviceInventory
{
  public Task<DeviceReport> CaptureAsync(CancellationToken cancellationToken = default)
  {
    var (memoryTotal, memoryInUse) = WindowsSystem.PhysicalMemory();
    var (storageTotalGb, storageUsedGb) = PortableInventory.SystemDiskUsage();
    var (ipV4, ipV6, macAddresses) = PortableInventory.Network();

    var report = new DeviceReport
    {
      MachineName = WindowsSystem.ComputerName(COMPUTER_NAME_FORMAT.ComputerNamePhysicalDnsHostname) ?? Environment.MachineName,
      DnsName = WindowsSystem.ComputerName(COMPUTER_NAME_FORMAT.ComputerNamePhysicalDnsFullyQualified) ?? SafeDnsName(),
      AgentVersion = PortableInventory.AgentVersion,
      Platform = DevicePlatform.Windows,
      OsDescription = RuntimeInformation.OSDescription,
      OsArchitecture = RuntimeInformation.OSArchitecture,
      Is64BitOs = Environment.Is64BitOperatingSystem,
      CpuCores = Environment.ProcessorCount,
      CpuLoad = cpu.Current,
      MemoryTotalGb = PortableInventory.ToGb(memoryTotal),
      MemoryUsedGb = PortableInventory.ToGb(memoryInUse),
      StorageTotalGb = storageTotalGb,
      StorageUsedGb = storageUsedGb,
      LoggedOnUsers = WindowsSystem.ActiveUsers(),
      MacAddresses = macAddresses,
      LocalIpV4 = ipV4,
      LocalIpV6 = ipV6,
      Disks = PortableInventory.FixedDisks(),
    };

    return Task.FromResult(report);
  }

  private static string SafeDnsName()
  {
    try
    {
      return Dns.GetHostName();
    }
    catch (Exception)
    {
      return Environment.MachineName;
    }
  }
}
