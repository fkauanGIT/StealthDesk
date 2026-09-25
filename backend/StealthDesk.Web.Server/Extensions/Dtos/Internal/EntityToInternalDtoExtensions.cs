namespace StealthDesk.Web.Server.Extensions.Dtos.Internal;

public static class EntityToInternalDtoExtensions
{
  public static InternalDtos.DeviceResponseDto ToInternalResponseDto(this Device device, bool isOutdated)
  {
    return new InternalDtos.DeviceResponseDto(
      device.Name,
      device.AgentVersion,
      device.CpuUtilization,
      device.Id,
      device.Is64Bit,
      device.IsOnline,
      device.LastSeen,
      device.OsArchitecture,
      device.Platform,
      device.ProcessorCount,
      device.ConnectionId,
      device.OsDescription,
      device.TenantId,
      device.TotalMemory,
      device.TotalStorage,
      device.UsedMemory,
      device.UsedStorage,
      device.CurrentUsers,
      device.MacAddresses,
      device.PublicIpV4,
      device.PublicIpV6,
      device.LocalIpV4,
      device.LocalIpV6,
      device.Drives,
      isOutdated,
      device.DnsHostName)
    {
      Alias = device.Alias
    };
  }
}
