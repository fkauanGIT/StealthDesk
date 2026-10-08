using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.Devices;

public static class DeviceEndpoints
{
  // The list holds only the devices the caller may read. A device they may not read answers 404, like one that
  // doesn't exist, so asking can't reveal that it does.
  public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder endpoints)
  {
    var devices = endpoints.MapGroup(string.Empty).RequireAuthorization().ChecksPermission(PermissionNames.DeviceRead);

    devices.MapGet(Routes.Devices, async (ClaimsPrincipal user, IDeviceAccess access, StealthDeskDb db, CancellationToken cancellationToken) =>
    {
      var scope = await access.ForAsync(user, cancellationToken);
      var devices = await scope.Apply(db.Devices.AsNoTracking())
        .OrderBy(x => x.Name)
        .ToListAsync(cancellationToken);

      return devices.Select(ToSummary);
    });

    devices.MapGet($"{Routes.Devices}/{{id:guid}}", async (
      Guid id,
      ClaimsPrincipal user,
      IAuthorizationService authorization,
      StealthDeskDb db,
      CancellationToken cancellationToken) =>
    {
      var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
      if (device is null || !(await authorization.AuthorizeAsync(user, device, PermissionPolicies.For(PermissionNames.DeviceRead))).Succeeded)
      {
        return Results.NotFound();
      }

      return Results.Ok(ToSummary(device));
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
