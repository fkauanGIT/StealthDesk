namespace StealthDesk.Web.Server.Persistence;

/// <summary>Users of a tenant grouped together, so permissions can be granted to all of them at once.</summary>
public class UserGroupRecord
{
  public const int NameMax = 100;
  public const int DescriptionMax = 500;

  public Guid Id { get; set; }
  public Guid TenantId { get; set; }
  public TenantRecord? Tenant { get; set; }
  public string Name { get; set; } = string.Empty;
  public string? Description { get; set; }
  public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
  public List<UserGroupMemberRecord> Members { get; set; } = [];
}

/// <summary>A user's membership of a group: they receive every assignment the group has.</summary>
public class UserGroupMemberRecord
{
  public Guid UserGroupId { get; set; }
  public UserGroupRecord? UserGroup { get; set; }
  public Guid UserId { get; set; }
  public UserRecord? User { get; set; }
}
