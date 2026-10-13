namespace StealthDesk.Contracts.AuthorizationLogs;

/// <summary>What an authorization change log entry records. Stored as readable text, to make raw queries easy.</summary>
public static class AuthorizationChangeActions
{
  public const string PermissionAssignmentCreated = "permission-assignment-created";
  public const string PermissionAssignmentDeleted = "permission-assignment-deleted";
  public const string PermissionAssignmentsSeeded = "permission-assignments-seeded";
  public const string PermissionAssignmentUpdated = "permission-assignment-updated";
  public const string PersonalAccessTokenCreated = "personal-access-token-created";
  public const string PersonalAccessTokenDeleted = "personal-access-token-deleted";
  public const string UserGroupCreated = "user-group-created";
  public const string UserGroupDeleted = "user-group-deleted";
  public const string UserGroupMembersAdded = "user-group-members-added";
  public const string UserGroupMembersRemoved = "user-group-members-removed";
  public const string UserGroupUpdated = "user-group-updated";
}

/// <summary>Who made the change.</summary>
public static class AuthorizationChangeActors
{
  public const string ServiceAccount = "service-account";

  /// <summary>The server itself, e.g. granting presets at registration.</summary>
  public const string System = "system";

  public const string User = "user";
}

/// <summary>What the change was made to.</summary>
public static class AuthorizationChangeTargets
{
  public const string PermissionAssignment = "PermissionAssignment";
  public const string PersonalAccessToken = "PersonalAccessToken";
  public const string User = "User";
  public const string UserGroup = "UserGroup";
}
