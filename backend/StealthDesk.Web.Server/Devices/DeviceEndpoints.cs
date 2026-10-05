namespace StealthDesk.Web.Server.Devices;

public static class DeviceEndpoints
{
  // Signed-in users only; the database filters by their tenant, so a device of another tenant is simply not found.
  public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder endpoints)
  {
    var devices = endpoints.MapGroup(string.Empty).RequireAuthorization();

    devices.MapGet(Routes.Devices, async (StealthDeskDb db, CancellationToken cancellationToken) =>
    {
      var devices = await db.Devices
        .AsNoTracking()
        .OrderBy(x => x.Name)
        .ToListAsync(cancellationToken);

      return devices.Select(ToSummary);
    });

    devices.MapGet($"{Routes.Devices}/{{id:guid}}", async (Guid id, StealthDeskDb db, CancellationToken cancellationToken) =>
    {
      var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
      return device is null ? Results.NotFound() : Results.Ok(ToSummary(device));
    });

    return endpoints;
  }

  public static DeviceSummary ToSummary(DeviceRecord device) => new()
  {
    Id = device.Id,
    TenantId = device.TenantId,
    Name = device.Name,
    Alias = device.Alias,
    DnsName = device.DnsName,
    AgentVersion = device.AgentVersion,
    Platform = device.Platform,
    OsDescription = device.OsDescription,
    OsArchitecture = device.OsArchitecture,
    Is64BitOs = device.Is64BitOs,
    CpuCores = device.CpuCores,
    CpuLoad = device.CpuLoad,
    MemoryTotalGb = device.MemoryTotalGb,
    MemoryUsedGb = device.MemoryUsedGb,
    StorageTotalGb = device.StorageTotalGb,
    StorageUsedGb = device.StorageUsedGb,
    LoggedOnUsers = device.LoggedOnUsers,
    MacAddresses = device.MacAddresses,
    LocalIpV4 = device.LocalIpV4,
    LocalIpV6 = device.LocalIpV6,
    PublicIpV4 = device.PublicIpV4,
    PublicIpV6 = device.PublicIpV6,
    Disks = device.Disks,
    IsOnline = device.IsOnline,
    LastSeen = device.LastSeen,
  };
}
