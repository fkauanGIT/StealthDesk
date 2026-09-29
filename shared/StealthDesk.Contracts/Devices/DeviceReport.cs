using System.Runtime.InteropServices;
using MessagePack;

namespace StealthDesk.Contracts.Devices;

/// <summary>
/// What an agent knows about the machine it runs on, sent to the server with every heartbeat.
/// Memory and storage are in gigabytes; <see cref="CpuLoad"/> goes from 0 to 1.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record DeviceReport
{
  public Guid DeviceId { get; init; }

  /// <summary>Empty until the device belongs to a tenant; the server may assign one.</summary>
  public Guid TenantId { get; init; }

  public string MachineName { get; init; } = string.Empty;
  public string DnsName { get; init; } = string.Empty;
  public string AgentVersion { get; init; } = string.Empty;

  public DevicePlatform Platform { get; init; }
  public string OsDescription { get; init; } = string.Empty;
  public Architecture OsArchitecture { get; init; }
  public bool Is64BitOs { get; init; }

  public int CpuCores { get; init; }
  public double CpuLoad { get; init; }
  public double MemoryTotalGb { get; init; }
  public double MemoryUsedGb { get; init; }
  public double StorageTotalGb { get; init; }
  public double StorageUsedGb { get; init; }

  public IReadOnlyList<string> LoggedOnUsers { get; init; } = [];
  public IReadOnlyList<string> MacAddresses { get; init; } = [];
  public string LocalIpV4 { get; init; } = string.Empty;
  public string LocalIpV6 { get; init; } = string.Empty;
  public IReadOnlyList<DiskInfo> Disks { get; init; } = [];
}
