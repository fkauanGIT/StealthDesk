namespace StealthDesk.Contracts.Permissions;

/// <summary>
/// Every permission the server knows, named "area.subject.action". A feature adds its own permissions when it
/// arrives; there are none for features that don't exist yet.
/// </summary>
public static class PermissionNames
{
  // Server administration: only meaningful at server scope.
  public const string ServerAuthorizationLogsRead = "server.authorization-logs.read";
  public const string ServerPermissionsRead = "server.permissions.read";
  public const string ServerPermissionsWrite = "server.permissions.write";
  public const string ServerServiceAccountsRead = "server.service-accounts.read";
  public const string ServerServiceAccountsRotateCredentials = "server.service-accounts.rotate-credentials";
  public const string ServerServiceAccountsWrite = "server.service-accounts.write";
  public const string ServerSettingsWrite = "server.settings.write";
  public const string ServerTenantsDelete = "server.tenants.delete";
  public const string ServerTenantsRead = "server.tenants.read";
  public const string ServerTenantsWrite = "server.tenants.write";

  // A tenant and the people in it.
  public const string TenantAuthorizationLogsRead = "tenant.authorization-logs.read";
  public const string TenantPermissionsDeny = "tenant.permissions.deny";
  public const string TenantPermissionsRead = "tenant.permissions.read";
  public const string TenantPermissionsWrite = "tenant.permissions.write";
  public const string TenantRead = "tenant.read";
  public const string TenantSettingsRead = "tenant.settings.read";
  public const string TenantSettingsWrite = "tenant.settings.write";
  public const string TenantUserGroupsRead = "tenant.user-groups.read";
  public const string TenantUserGroupsWrite = "tenant.user-groups.write";
  public const string TenantUsersDelete = "tenant.users.delete";
  public const string TenantUsersRead = "tenant.users.read";
  public const string TenantUsersWrite = "tenant.users.write";
  public const string UserGroupAssignUsers = "user-group.assign-users";

  // Devices.
  public const string DeviceRead = "device.read";

  // Credentials and agents.
  public const string AgentInstall = "agent.install";
  public const string InstallerKeyManageAll = "installer-key.manage-all";
  public const string InstallerKeyRead = "installer-key.read";
  public const string InstallerKeyWrite = "installer-key.write";
  public const string PersonalAccessTokenOthersRead = "personal-access-token.others.read";
  public const string PersonalAccessTokenOthersWrite = "personal-access-token.others.write";
  public const string PersonalAccessTokenSelfRead = "personal-access-token.self.read";
  public const string PersonalAccessTokenSelfWrite = "personal-access-token.self.write";
  public const string ServiceAccountRead = "service-account.read";
  public const string ServiceAccountRotateCredentials = "service-account.rotate-credentials";
  public const string ServiceAccountWrite = "service-account.write";
}
