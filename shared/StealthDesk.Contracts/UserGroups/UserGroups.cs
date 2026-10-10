namespace StealthDesk.Contracts.UserGroups;

public sealed record UserGroupSummary(Guid Id, string Name, string? Description, DateTimeOffset CreatedAt, int MemberCount);

public sealed class UserGroupList
{
  public IReadOnlyList<UserGroupSummary> Items { get; init; } = [];
}

public sealed record UserGroupMember(Guid UserId, string UserName, DateTimeOffset? LastSignIn);

public sealed record UserGroupDetail(Guid Id, string Name, string? Description, DateTimeOffset CreatedAt, IReadOnlyList<UserGroupMember> Members);

/// <summary>Creates a group, or renames and describes one.</summary>
public sealed record UserGroupRequest(string Name, string? Description = null)
{
  public const int NameMax = 100;
  public const int DescriptionMax = 500;
}

/// <summary>The users to add to a group or remove from it.</summary>
public sealed record UserGroupMembersRequest(IReadOnlyList<Guid> UserIds);
