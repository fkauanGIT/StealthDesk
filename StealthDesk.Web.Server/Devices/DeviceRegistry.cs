using System.Net.Sockets;

namespace StealthDesk.Web.Server.Devices;

/// <summary>Stores what agents report and tracks which devices are connected.</summary>
public interface IDeviceRegistry
{
  /// <summary>A device of any tenant, as stored, or null if the device is unknown.</summary>
  Task<DeviceRecord?> FindAsync(Guid deviceId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Saves the report and marks the device online, signing with <paramref name="publicKey"/> from now on. An unknown
  /// device is only created when <paramref name="allowNew"/> is true; a known device can't be moved to another tenant.
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
  /// Returns the device marked offline, or null if nothing changed.
  /// </summary>
  Task<DeviceRecord?> MarkOfflineAsync(Guid deviceId, string connectionId, DateTimeOffset lastSeen, CancellationToken cancellationToken = default);
}

public sealed class DeviceRegistry(StealthDeskDb db, ILogger<DeviceRegistry> logger) : IDeviceRegistry
{
  public Task<DeviceRecord?> FindAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
    db.Devices.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, cancellationToken);

  public async Task<Outcome<DeviceRecord>> SaveReportAsync(
    DeviceReport report,
    ReportOrigin connection,
    string publicKey,
    bool allowNew,
    CancellationToken cancellationToken = default)
  {
    var device = report.DeviceId == Guid.Empty
      ? null
      : await db.Devices.Include(x => x.GroupMemberships).FirstOrDefaultAsync(x => x.Id == report.DeviceId, cancellationToken);

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

    // A reinstalled agent brings a new key; the gateway only passes one it accepted.
    device.PublicKey = publicKey;
    CopyReport(report, device);
    device.ConnectionId = connection.ConnectionId;
    device.IsOnline = true;
    device.LastSeen = connection.ReceivedAt;
    RecordPublicAddress(device, connection);
    if (db.Entry(device).State != EntityState.Added)
    {
      // Written even when it was already online when read: the old connection closing may have marked it offline
      // since, and only properties that changed since the read would be saved.
      db.Entry(device).Property(x => x.IsOnline).IsModified = true;
    }

    await db.SaveChangesAsync(cancellationToken);
    return device;
  }

  public async Task<DeviceRecord?> MarkOfflineAsync(Guid deviceId, string connectionId, DateTimeOffset lastSeen, CancellationToken cancellationToken = default)
  {
    var onThisConnection = db.Devices.Where(x => x.Id == deviceId && x.ConnectionId == connectionId);

    // One conditional update, not a read and then a write: a report on the newer connection landing between the two
    // would be overwritten, and a connected device shown offline.
    var changed = db.Database.IsRelational()
      ? await onThisConnection.ExecuteUpdateAsync(
        set => set
          .SetProperty(x => x.IsOnline, false)
          .SetProperty(x => x.ConnectionId, string.Empty)
          .SetProperty(x => x.LastSeen, lastSeen),
        cancellationToken)
      : await MarkOfflineInMemoryAsync(onThisConnection, lastSeen, cancellationToken);

    if (changed == 0)
    {
      logger.LogDebug("Device {DeviceId} already moved to a newer connection; leaving it online.", deviceId);
      return null;
    }

    return await db.Devices.AsNoTracking().Include(x => x.GroupMemberships).FirstAsync(x => x.Id == deviceId, cancellationToken);
  }

  // The in-memory provider can't run a conditional update, so it keeps the race; tests that race connections run on
  // PostgreSQL.
  private async Task<int> MarkOfflineInMemoryAsync(IQueryable<DeviceRecord> onThisConnection, DateTimeOffset lastSeen, CancellationToken cancellationToken)
  {
    if (await onThisConnection.FirstOrDefaultAsync(cancellationToken) is not { } device)
    {
      return 0;
    }

    device.IsOnline = false;
    device.ConnectionId = string.Empty;
    device.LastSeen = lastSeen;
    await db.SaveChangesAsync(cancellationToken);
    return 1;
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
