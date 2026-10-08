namespace StealthDesk.Contracts.Permissions;

/// <summary>What a permission assignment reaches, from the whole server down to one device.</summary>
public enum PermissionScopeKind
{
  /// <summary>Never a real scope: an assignment whose scope was left out reaches nothing.</summary>
  Unknown = 0,

  /// <summary>Everything on the server, every tenant included.</summary>
  Server = 1,

  /// <summary>One tenant and everything in it.</summary>
  Tenant = 2,

  /// <summary>One device.</summary>
  Device = 3,

  /// <summary>One user group, for managing that group.</summary>
  UserGroup = 4,
}

public enum PermissionEffect
{
  Allow = 0,

  /// <summary>Wins over any allow that reaches the same resource.</summary>
  Deny = 1,
}

/// <summary>Who an assignment is for.</summary>
public enum PermissionPrincipalKind
{
  User = 0,

  /// <summary>Every member of the group receives the assignment.</summary>
  UserGroup = 1,

  ServiceAccount = 2,

  PersonalAccessToken = 3,
}
