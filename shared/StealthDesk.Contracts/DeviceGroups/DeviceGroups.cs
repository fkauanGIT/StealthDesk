namespace StealthDesk.Contracts.DeviceGroups;

public sealed record DeviceGroupSummary(Guid Id, string Name, string? Description, DateTimeOffset CreatedAt, int DeviceCount);

public sealed class DeviceGroupList
{
  public IReadOnlyList<DeviceGroupSummary> Items { get; init; } = [];
}

/// <summary>A device as a group lists it.</summary>
public sealed record DeviceGroupDevice(Guid Id, string Name, bool IsOnline, DateTimeOffset LastSeen);

public sealed record DeviceGroupDetail(Guid Id, string Name, string? Description, DateTimeOffset CreatedAt, IReadOnlyList<DeviceGroupDevice> Devices);

public sealed record DeviceGroupRequest(string Name, string? Description = null)
{
  public const int NameMax = 100;
  public const int DescriptionMax = 500;
}

/// <summary>Devices to add to a group or remove from it.</summary>
/// <param name="FromGroupId">When adding, the group they move from, left in the same change.</param>
public sealed record DeviceGroupMembersRequest(IReadOnlyList<Guid> DeviceIds, Guid? FromGroupId = null);
