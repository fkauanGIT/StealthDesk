namespace StealthDesk.Contracts.Permissions;

/// <summary>
/// Named bundles of permissions for common roles. A preset is only a way to grant many permissions at once: what
/// the user holds afterwards are the individual assignments, which can be changed one by one.
/// </summary>
public static class PermissionPresets
{
  public const string AgentInstaller = "Agent Installer";
  public const string DeviceSuperuser = "Device Superuser";
  public const string InstallerKeyManager = "Installer Key Manager";
  public const string SelfService = "Self Service";
  public const string ServerAdministrator = "Server Administrator";
  public const string ServiceAccountManager = "Service Account Manager";
  public const string TenantAdministrator = "Tenant Administrator";

  /// <summary>What the first user of a server receives, on top of being a tenant's creator.</summary>
  public static IReadOnlyList<string> FirstUser { get; } = [ServerAdministrator];

  /// <summary>What whoever creates a tenant receives in it.</summary>
  public static IReadOnlyList<string> TenantCreator { get; } = [TenantAdministrator, DeviceSuperuser, AgentInstaller, InstallerKeyManager];

  /// <summary>What every signed-in person receives: managing their own tokens.</summary>
  public static IReadOnlyList<string> Baseline { get; } = [SelfService];

  public static IReadOnlyDictionary<string, IReadOnlyList<string>> All { get; } = new Dictionary<string, IReadOnlyList<string>>
  {
    [ServerAdministrator] =
    [
      PermissionNames.ServerAuthorizationLogsRead,
      PermissionNames.ServerPermissionsRead,
      PermissionNames.ServerPermissionsWrite,
      PermissionNames.ServerServiceAccountsRead,
      PermissionNames.ServerServiceAccountsRotateCredentials,
      PermissionNames.ServerServiceAccountsWrite,
      PermissionNames.ServerSettingsWrite,
      PermissionNames.ServerTenantsDelete,
      PermissionNames.ServerTenantsRead,
      PermissionNames.ServerTenantsWrite,
      PermissionNames.TenantAuthorizationLogsRead,
      PermissionNames.TenantPermissionsRead,
    ],
    [TenantAdministrator] =
    [
      PermissionNames.TenantRead,
      PermissionNames.TenantSettingsRead,
      PermissionNames.TenantSettingsWrite,
      PermissionNames.TenantUsersRead,
      PermissionNames.TenantUsersWrite,
      PermissionNames.TenantUsersDelete,
      PermissionNames.TenantUserGroupsRead,
      PermissionNames.TenantUserGroupsWrite,
      PermissionNames.UserGroupAssignUsers,
      PermissionNames.TenantPermissionsRead,
      PermissionNames.TenantPermissionsWrite,
      PermissionNames.TenantPermissionsDeny,
      PermissionNames.TenantAuthorizationLogsRead,
      PermissionNames.PersonalAccessTokenSelfRead,
      PermissionNames.PersonalAccessTokenSelfWrite,
      PermissionNames.PersonalAccessTokenOthersRead,
      PermissionNames.PersonalAccessTokenOthersWrite,
      PermissionNames.ServiceAccountRead,
      PermissionNames.ServiceAccountWrite,
      PermissionNames.ServiceAccountRotateCredentials,
      PermissionNames.InstallerKeyRead,
      PermissionNames.InstallerKeyWrite,
      PermissionNames.InstallerKeyManageAll,
      PermissionNames.AgentInstall,
    ],
    [DeviceSuperuser] =
    [
      PermissionNames.DeviceRead,
    ],
    [AgentInstaller] =
    [
      PermissionNames.AgentInstall,
      PermissionNames.InstallerKeyRead,
      PermissionNames.InstallerKeyWrite,
    ],
    [InstallerKeyManager] =
    [
      PermissionNames.InstallerKeyRead,
      PermissionNames.InstallerKeyWrite,
      PermissionNames.AgentInstall,
    ],
    [ServiceAccountManager] =
    [
      PermissionNames.ServiceAccountRead,
      PermissionNames.ServiceAccountWrite,
      PermissionNames.ServiceAccountRotateCredentials,
    ],
    [SelfService] =
    [
      PermissionNames.PersonalAccessTokenSelfRead,
      PermissionNames.PersonalAccessTokenSelfWrite,
    ],
  };

  /// <summary>The preset's permissions; none for an unknown name.</summary>
  public static IReadOnlyList<string> PermissionsOf(string preset) => All.TryGetValue(preset, out var permissions) ? permissions : [];
}
