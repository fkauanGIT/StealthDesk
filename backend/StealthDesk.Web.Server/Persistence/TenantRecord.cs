namespace StealthDesk.Web.Server.Persistence;

/// <summary>An organization that owns devices.</summary>
public class TenantRecord
{
  public const int NameMax = 100;

  public Guid Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
  public List<DeviceRecord> Devices { get; set; } = [];
  public List<UserRecord> Users { get; set; } = [];
}
