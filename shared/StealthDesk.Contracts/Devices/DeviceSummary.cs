using System.Runtime.InteropServices;

namespace StealthDesk.Contracts.Devices;

/// <summary>A device as the server exposes it through the REST API.</summary>
public sealed record DeviceSummary
{
  public Guid Id { get; init; }
  public Guid TenantId { get; init; }
  public string Name { get; init; } = string.Empty;
  public string Alias { get; init; } = string.Empty;
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
  public string PublicIpV4 { get; init; } = string.Empty;
  public string PublicIpV6 { get; init; } = string.Empty;
  public IReadOnlyList<DiskInfo> Disks { get; init; } = [];

  public bool IsOnline { get; init; }
  public DateTimeOffset LastSeen { get; init; }
}
