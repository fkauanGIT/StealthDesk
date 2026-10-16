namespace StealthDesk.Web.Server.Persistence;

/// <summary>Devices of a tenant grouped together, e.g. by department, so device permissions can cover all of them.</summary>
public class DeviceGroupRecord
{
  public const int NameMax = 100;
  public const int DescriptionMax = 500;

  public Guid Id { get; set; }
  public Guid TenantId { get; set; }
  public TenantRecord? Tenant { get; set; }
  public string Name { get; set; } = string.Empty;
  public string? Description { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
  public List<DeviceGroupMemberRecord> Members { get; set; } = [];
}

/// <summary>A device's membership of a group: what the group is granted reaches it.</summary>
public class DeviceGroupMemberRecord
{
  public Guid DeviceGroupId { get; set; }
  public DeviceGroupRecord? DeviceGroup { get; set; }
  public Guid DeviceId { get; set; }
  public DeviceRecord? Device { get; set; }
}
