using System.Net.Sockets;

namespace StealthDesk.Web.Server.Devices;

/// <summary>Stores what agents report and tracks which devices are connected.</summary>
public interface IDeviceRegistry
{
  /// <summary>The key a device registered with, or null if the device is unknown.</summary>
  Task<string?> FindPublicKeyAsync(Guid deviceId, CancellationToken cancellationToken = default);

  /// <summary>Tenant of a known device, or null if the device is unknown.</summary>
  Task<Guid?> FindTenantAsync(Guid deviceId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Saves the report and marks the device online. An unknown device is only created when
  /// <paramref name="allowNew"/> is true; a known device can't be moved to another tenant.
  /// </summary>
  Task<Outcome<DeviceRecord>> SaveReportAsync(
    DeviceReport report,
    ReportOrigin connection,
    string publicKey,
    bool allowNew,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Marks the device offline, but only if <paramref name="connectionId"/> is still its current connection:
  /// after a quick reconnect, the old connection closing must not hide the new one.
  /// </summary>
  Task<bool> MarkOfflineAsync(Guid deviceId, string connectionId, DateTimeOffset lastSeen, CancellationToken cancellationToken = default);
}

public sealed class DeviceRegistry(StealthDeskDb db, ILogger<DeviceRegistry> logger) : IDeviceRegistry
{
  public Task<string?> FindPublicKeyAsync(Guid deviceId, CancellationToken cancellationToken = default)
  {
    return db.Devices
      .Where(x => x.Id == deviceId)
      .Select(x => x.PublicKey)
      .FirstOrDefaultAsync(cancellationToken);
  }

  public async Task<Guid?> FindTenantAsync(Guid deviceId, CancellationToken cancellationToken = default)
  {
    var tenant = await db.Devices
      .Where(x => x.Id == deviceId)
      .Select(x => (Guid?)x.TenantId)
      .FirstOrDefaultAsync(cancellationToken);
    return tenant;
  }

  public async Task<Outcome<DeviceRecord>> SaveReportAsync(
    DeviceReport report,
    ReportOrigin connection,
    string publicKey,
    bool allowNew,
    CancellationToken cancellationToken = default)
  {
    var device = report.DeviceId == Guid.Empty
      ? null
      : await db.Devices.FirstOrDefaultAsync(x => x.Id == report.DeviceId, cancellationToken);

    if (device is null)
    {
      if (!allowNew)
      {
        return Outcome.Failure<DeviceRecord>("Unknown device.");
      }

      // An empty id gets a new one from the database layer; the agent adopts it from the receipt.
      device = new DeviceRecord { Id = report.DeviceId, TenantId = report.TenantId, PublicKey = publicKey };
      db.Devices.Add(device);
    }
    else if (device.TenantId != report.TenantId)
    {
      return Outcome.Failure<DeviceRecord>("The device belongs to another tenant.");
    }

    CopyReport(report, device);
    device.ConnectionId = connection.ConnectionId;
    device.IsOnline = true;
    device.LastSeen = connection.ReceivedAt;
    RecordPublicAddress(device, connection);

    await db.SaveChangesAsync(cancellationToken);
    return device;
  }

  public async Task<bool> MarkOfflineAsync(Guid deviceId, string connectionId, DateTimeOffset lastSeen, CancellationToken cancellationToken = default)
  {
    var device = await db.Devices.FirstOrDefaultAsync(
      x => x.Id == deviceId && x.ConnectionId == connectionId,
      cancellationToken);

    if (device is null)
    {
      logger.LogDebug("Device {DeviceId} already moved to a newer connection; leaving it online.", deviceId);
      return false;
    }

    device.IsOnline = false;
    device.ConnectionId = string.Empty;
    device.LastSeen = lastSeen;
    await db.SaveChangesAsync(cancellationToken);
    return true;
  }

  // Reports come from machines we don't control: oversized text is cut to fit instead of failing the save.
  private static void CopyReport(DeviceReport report, DeviceRecord device)
  {
    device.Name = Fit(report.MachineName, DeviceRecord.NameMax);
    device.DnsName = Fit(report.DnsName, DeviceRecord.DnsNameMax);
    device.AgentVersion = Fit(report.AgentVersion, DeviceRecord.AgentVersionMax);
    device.Platform = report.Platform;
    device.OsDescription = Fit(report.OsDescription, DeviceRecord.OsDescriptionMax);
    device.OsArchitecture = report.OsArchitecture;
    device.Is64BitOs = report.Is64BitOs;
    device.CpuCores = report.CpuCores;
    device.CpuLoad = report.CpuLoad;
    device.MemoryTotalGb = report.MemoryTotalGb;
    device.MemoryUsedGb = report.MemoryUsedGb;
    device.StorageTotalGb = report.StorageTotalGb;
    device.StorageUsedGb = report.StorageUsedGb;
    device.LoggedOnUsers = [.. report.LoggedOnUsers];
    device.MacAddresses = [.. report.MacAddresses];
    device.LocalIpV4 = Fit(report.LocalIpV4, DeviceRecord.IpV4Max);
    device.LocalIpV6 = Fit(report.LocalIpV6, DeviceRecord.IpV6Max);
    device.Disks = [.. report.Disks];
  }

  private static string Fit(string? value, int maxLength) =>
    string.IsNullOrEmpty(value) ? string.Empty : value.Length <= maxLength ? value : value[..maxLength];

  private static void RecordPublicAddress(DeviceRecord device, ReportOrigin connection)
  {
    var address = connection.RemoteAddress;
    if (address is null)
    {
      return;
    }

    if (address.IsIPv4MappedToIPv6)
    {
      address = address.MapToIPv4();
    }

    if (address.AddressFamily == AddressFamily.InterNetwork)
    {
      device.PublicIpV4 = address.ToString();
    }
    else if (address.AddressFamily == AddressFamily.InterNetworkV6)
    {
      device.PublicIpV6 = Fit(address.ToString(), DeviceRecord.IpV6Max);
    }
  }
}
