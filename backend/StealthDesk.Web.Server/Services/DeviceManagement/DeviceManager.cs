using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using StealthDesk.Libraries.Api.Contracts.Dtos.HubDtos;

namespace StealthDesk.Web.Server.Services.DeviceManagement;

/// <summary>
/// Persists devices reported by agents.
/// </summary>
public interface IDeviceManager
{
  /// <summary>
  /// Creates the device if its id is unknown, otherwise updates it.
  /// </summary>
  Task<Device> AddOrUpdate(DeviceUpdateRequestDto deviceDto, DeviceConnectionContext context, string? publicKeyBase64 = null);

  /// <summary>
  /// Marks the device offline and records when it was last seen.
  /// Fails if the device doesn't exist.
  /// </summary>
  Task<Result<Device>> MarkDeviceOffline(Guid deviceId, DateTimeOffset lastSeen);

  /// <summary>
  /// Updates an existing device. Fails if the device doesn't exist or belongs to another tenant.
  /// </summary>
  Task<Result<Device>> UpdateDevice(DeviceUpdateRequestDto deviceDto, DeviceConnectionContext context, string? publicKeyBase64 = null);
}

public class DeviceManager(
  AppDb appDb,
  ILogger<DeviceManager> logger) : IDeviceManager
{
  private const string DeviceNotFound = "Device does not exist in the database.";

  private static readonly ConcurrentDictionary<Type, ImmutableDictionary<string, PropertyInfo>> _dtoPropertiesByType = [];

  private readonly AppDb _appDb = appDb;
  private readonly ILogger<DeviceManager> _logger = logger;

  public async Task<Device> AddOrUpdate(DeviceUpdateRequestDto deviceDto, DeviceConnectionContext context, string? publicKeyBase64 = null)
  {
    var existing = await FindDevice(deviceDto.Id);
    var device = existing ?? new Device();
    var state = existing is null ? EntityState.Added : EntityState.Modified;

    await ApplyUpdate(device, deviceDto, context, state, publicKeyBase64);
    return device;
  }

  public async Task<Result<Device>> MarkDeviceOffline(Guid deviceId, DateTimeOffset lastSeen)
  {
    var device = await FindDevice(deviceId);
    if (device is null)
    {
      return Result.Fail<Device>(DeviceNotFound);
    }

    device.IsOnline = false;
    device.LastSeen = lastSeen;
    // A new connection gets a new id, so the old one is meaningless once offline.
    device.ConnectionId = string.Empty;

    await _appDb.SaveChangesAsync();
    return Result.Ok(device);
  }

  public async Task<Result<Device>> UpdateDevice(DeviceUpdateRequestDto deviceDto, DeviceConnectionContext context, string? publicKeyBase64 = null)
  {
    var device = await FindDevice(deviceDto.Id);
    if (device is null)
    {
      return Result.Fail<Device>(DeviceNotFound);
    }

    if (IsTenantChange(device, deviceDto))
    {
      return Result.Fail<Device>("Device belongs to a different tenant.");
    }

    await ApplyUpdate(device, deviceDto, context, EntityState.Modified, publicKeyBase64);
    return Result.Ok(device);
  }

  private static bool IsTenantChange(Device device, DeviceUpdateRequestDto deviceDto)
  {
    return device.TenantId != Guid.Empty && device.TenantId != deviceDto.TenantId;
  }

  private static void CopyDtoValues<TDto>(EntityEntry entry, TDto dto, params string[] skip)
    where TDto : notnull
  {
    var dtoProperties = _dtoPropertiesByType.GetOrAdd(typeof(TDto), type => type
      .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
      .ToImmutableDictionary(x => x.Name));

    foreach (var column in entry.Properties)
    {
      var name = column.Metadata.Name;
      if (skip.Contains(name) || !dtoProperties.TryGetValue(name, out var dtoProperty))
      {
        continue;
      }

      var value = dtoProperty.GetValue(dto);
      var clrType = column.Metadata.ClrType;

      if (value is null)
      {
        var isNonNullableValueType = clrType.IsValueType && Nullable.GetUnderlyingType(clrType) is null;
        var isNonNullableReference = !clrType.IsValueType && !column.Metadata.IsNullable;
        if (isNonNullableValueType || isNonNullableReference)
        {
          continue;
        }
      }

      // Agents are untrusted input: truncate strings to the column size instead of failing the save.
      if (value is string text &&
          clrType == typeof(string) &&
          column.Metadata.GetMaxLength() is int maxLength and > 0 &&
          text.Length > maxLength)
      {
        value = text[..maxLength];
      }

      column.CurrentValue = value;
    }
  }

  private async Task ApplyUpdate(
    Device device,
    DeviceUpdateRequestDto deviceDto,
    DeviceConnectionContext context,
    EntityState state,
    string? publicKeyBase64)
  {
    if (IsTenantChange(device, deviceDto))
    {
      throw new InvalidOperationException(
        $"Device {deviceDto.Id} belongs to tenant {device.TenantId} and cannot be moved to tenant {deviceDto.TenantId}.");
    }

    var entry = _appDb.Entry(device);
    await entry.Reference(x => x.Tenant).LoadAsync();
    entry.State = state;

    CopyDtoValues(entry, deviceDto, nameof(DeviceUpdateRequestDto.TenantId));

    device.TenantId = deviceDto.TenantId;
    device.Drives = [.. deviceDto.Drives];
    device.ConnectionId = context.ConnectionId;
    device.IsOnline = context.IsOnline;
    device.LastSeen = context.LastSeen;
    SetPublicIp(device, context);

    if (!string.IsNullOrEmpty(publicKeyBase64))
    {
      device.PublicKey = publicKeyBase64;
    }

    await _appDb.SaveChangesAsync();
  }

  private Task<Device?> FindDevice(Guid deviceId)
  {
    return _appDb.Devices
      .IgnoreQueryFilters()
      .FirstOrDefaultAsync(x => x.Id == deviceId);
  }

  private void SetPublicIp(Device device, DeviceConnectionContext context)
  {
    if (context.RemoteIpAddress is not { } ip)
    {
      return;
    }

    switch (ip.AddressFamily)
    {
      case AddressFamily.InterNetwork:
        device.PublicIpV4 = ip.ToString();
        break;
      case AddressFamily.InterNetworkV6:
        device.PublicIpV6 = ip.ToString();
        break;
      default:
        _logger.LogWarning("Unsupported IP address family: {AddressFamily}", ip.AddressFamily);
        break;
    }
  }
}
