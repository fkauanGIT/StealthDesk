using System.Runtime.InteropServices;

namespace StealthDesk.Web.Server.Persistence;

/// <summary>A device as stored by the server: the last report of its agent plus what the server observed.</summary>
public class DeviceRecord
{
  public const int NameMax = 100;
  public const int DnsNameMax = 255;
  public const int AgentVersionMax = 50;
  public const int OsDescriptionMax = 300;
  public const int IpV4Max = 15;
  public const int IpV6Max = 45;
  public const int PublicKeyMax = 64;

  public Guid Id { get; set; }
  public Guid TenantId { get; set; }
  public TenantRecord? Tenant { get; set; }
  public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

  // Reported by the agent.
  public string Name { get; set; } = string.Empty;
  public string DnsName { get; set; } = string.Empty;
  public string AgentVersion { get; set; } = string.Empty;
  public DevicePlatform Platform { get; set; }
  public string OsDescription { get; set; } = string.Empty;
  public Architecture OsArchitecture { get; set; }
  public bool Is64BitOs { get; set; }
  public int CpuCores { get; set; }
  public double CpuLoad { get; set; }
  public double MemoryTotalGb { get; set; }
  public double MemoryUsedGb { get; set; }
  public double StorageTotalGb { get; set; }
  public double StorageUsedGb { get; set; }
  public string[] LoggedOnUsers { get; set; } = [];
  public string[] MacAddresses { get; set; } = [];
  public string LocalIpV4 { get; set; } = string.Empty;
  public string LocalIpV6 { get; set; } = string.Empty;
  public List<DiskInfo> Disks { get; set; } = [];

  // Set by the server.
  public string Alias { get; set; } = string.Empty;
  public string PublicIpV4 { get; set; } = string.Empty;
  public string PublicIpV6 { get; set; } = string.Empty;

  /// <summary>Key the device proved its identity with on first contact. Only this key is trusted afterwards.</summary>
  public string PublicKey { get; set; } = string.Empty;
  public string ConnectionId { get; set; } = string.Empty;
  public bool IsOnline { get; set; }
  public DateTimeOffset LastSeen { get; set; }
}
