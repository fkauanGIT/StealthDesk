namespace StealthDesk.Contracts.AuthorizationLogs;

/// <summary>What an authorization change log entry records. Stored as readable text, to make raw queries easy.</summary>
public static class AuthorizationChangeActions
{
  public const string PermissionAssignmentsSeeded = "permission-assignments-seeded";
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
  public const string User = "User";
  public const string UserGroup = "UserGroup";
}
