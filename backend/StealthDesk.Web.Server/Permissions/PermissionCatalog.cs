using System.Collections.Frozen;
using StealthDesk.Contracts.Permissions;

namespace StealthDesk.Web.Server.Permissions;

/// <summary>A permission as the server describes it: for pages that grant it, and for checking where it applies.</summary>
/// <param name="Scopes">Where it can be granted, from the narrowest to the broadest.</param>
/// <param name="SelfRemovable">False when removing it from yourself would lock you out of managing access.</param>
public sealed record PermissionInfo(
  string Name,
  string Category,
  string DisplayName,
  string Description,
  IReadOnlyList<PermissionScopeKind> Scopes,
  bool SelfRemovable = true)
{
  /// <summary>Granted inside a tenant; otherwise it is about the server itself and only server scope makes sense.</summary>
  public bool AllowsTenantScope => Scopes.Contains(PermissionScopeKind.Tenant);

  /// <summary>The broadest scope that stays inside one tenant, used when a preset grants it to a tenant's user.</summary>
  public PermissionScopeKind PresetScope => AllowsTenantScope ? PermissionScopeKind.Tenant : Scopes[^1];
}

/// <summary>Every permission the server knows, with where each one can be granted.</summary>
public static class PermissionCatalog
{
  public static class Categories
  {
    public const string Agents = "Agents";
    public const string Devices = "Devices";
    public const string InstallerKeys = "Installer Keys";
    public const string PersonalAccessTokens = "Personal Access Tokens";
    public const string Server = "Server";
    public const string ServiceAccounts = "Service Accounts";
    public const string Tenant = "Tenant";
    public const string UserGroups = "User Groups";
  }

  private static readonly PermissionScopeKind[] ServerOnly = [PermissionScopeKind.Server];
  private static readonly PermissionScopeKind[] TenantOnly = [PermissionScopeKind.Tenant];

  // A device permission can be granted for one device, the whole tenant, or (for server principals) every tenant.
  private static readonly PermissionScopeKind[] Devices = [PermissionScopeKind.Device, PermissionScopeKind.Tenant, PermissionScopeKind.Server];
  private static readonly PermissionScopeKind[] UserGroups = [PermissionScopeKind.UserGroup, PermissionScopeKind.Tenant];

  private static readonly FrozenDictionary<string, PermissionInfo> Entries = new PermissionInfo[]
  {
    new(PermissionNames.ServerAuthorizationLogsRead, Categories.Server, "Read server authorization logs", "View the authorization change log of every tenant, server entries included.", ServerOnly),
    new(PermissionNames.ServerPermissionsRead, Categories.Server, "Read server permissions", "View permissions granted at server scope.", ServerOnly),
    new(PermissionNames.ServerPermissionsWrite, Categories.Server, "Manage server permissions", "Grant, change and remove permissions at server scope.", ServerOnly, SelfRemovable: false),
    new(PermissionNames.ServerServiceAccountsRead, Categories.Server, "Read server service accounts", "View server service accounts and their credentials.", ServerOnly),
    new(PermissionNames.ServerServiceAccountsWrite, Categories.Server, "Manage server service accounts", "Create and delete server service accounts.", ServerOnly),
    new(PermissionNames.ServerServiceAccountsRotateCredentials, Categories.Server, "Rotate server service account credentials", "Create and revoke credentials of server service accounts.", ServerOnly),
    new(PermissionNames.ServerSettingsWrite, Categories.Server, "Manage server settings", "Change the server's settings, such as the account rules.", ServerOnly),
    new(PermissionNames.ServerTenantsRead, Categories.Server, "Read tenants", "List every tenant on the server.", ServerOnly),
    new(PermissionNames.ServerTenantsWrite, Categories.Server, "Manage tenants", "Create and rename tenants.", ServerOnly),
    new(PermissionNames.ServerTenantsDelete, Categories.Server, "Delete tenants", "Delete a tenant with its users and devices.", ServerOnly),

    new(PermissionNames.TenantRead, Categories.Tenant, "Read tenant", "View the tenant's details.", TenantOnly),
    new(PermissionNames.TenantSettingsRead, Categories.Tenant, "Read tenant settings", "View the tenant's settings.", TenantOnly),
    new(PermissionNames.TenantSettingsWrite, Categories.Tenant, "Manage tenant settings", "Change the tenant's settings.", TenantOnly),
    new(PermissionNames.TenantUsersRead, Categories.Tenant, "Read users", "View the tenant's users.", TenantOnly),
    new(PermissionNames.TenantUsersWrite, Categories.Tenant, "Manage users", "Invite users and change them.", TenantOnly),
    new(PermissionNames.TenantUsersDelete, Categories.Tenant, "Delete users", "Remove users from the tenant.", TenantOnly),
    new(PermissionNames.TenantUserGroupsRead, Categories.Tenant, "Read user groups", "View the tenant's user groups.", TenantOnly),
    new(PermissionNames.TenantUserGroupsWrite, Categories.Tenant, "Manage user groups", "Create, rename and delete user groups.", TenantOnly),
    new(PermissionNames.TenantPermissionsRead, Categories.Tenant, "Read permissions", "View who holds which permission in the tenant.", TenantOnly),
    new(PermissionNames.TenantPermissionsWrite, Categories.Tenant, "Manage permissions", "Grant, change and remove allow permissions in the tenant.", TenantOnly, SelfRemovable: false),
    new(PermissionNames.TenantPermissionsDeny, Categories.Tenant, "Manage deny permissions", "Grant and change deny permissions, which win over any allow.", TenantOnly, SelfRemovable: false),
    new(PermissionNames.TenantAuthorizationLogsRead, Categories.Tenant, "Read authorization logs", "View the tenant's authorization change log.", TenantOnly),

    new(PermissionNames.UserGroupAssignUsers, Categories.UserGroups, "Assign users to a group", "Add users to a user group and remove them.", UserGroups),

    new(PermissionNames.DeviceRead, Categories.Devices, "Read devices", "View devices, their details and their live status.", Devices),

    new(PermissionNames.PersonalAccessTokenSelfRead, Categories.PersonalAccessTokens, "Read own tokens", "View your own personal access tokens.", TenantOnly),
    new(PermissionNames.PersonalAccessTokenSelfWrite, Categories.PersonalAccessTokens, "Manage own tokens", "Create and revoke your own personal access tokens.", TenantOnly),
    new(PermissionNames.PersonalAccessTokenOthersRead, Categories.PersonalAccessTokens, "Read others' tokens", "View the personal access tokens of other users in the tenant.", TenantOnly),
    new(PermissionNames.PersonalAccessTokenOthersWrite, Categories.PersonalAccessTokens, "Manage others' tokens", "Revoke the personal access tokens of other users in the tenant.", TenantOnly),

    new(PermissionNames.ServiceAccountRead, Categories.ServiceAccounts, "Read service accounts", "View the tenant's service accounts and their credentials.", TenantOnly),
    new(PermissionNames.ServiceAccountWrite, Categories.ServiceAccounts, "Manage service accounts", "Create and delete the tenant's service accounts.", TenantOnly),
    new(PermissionNames.ServiceAccountRotateCredentials, Categories.ServiceAccounts, "Rotate service account credentials", "Create and revoke credentials of the tenant's service accounts.", TenantOnly),

    new(PermissionNames.InstallerKeyRead, Categories.InstallerKeys, "Read installer keys", "View your agent installer keys.", TenantOnly),
    new(PermissionNames.InstallerKeyWrite, Categories.InstallerKeys, "Manage installer keys", "Create and revoke agent installer keys.", TenantOnly),
    new(PermissionNames.InstallerKeyManageAll, Categories.InstallerKeys, "Manage everyone's installer keys", "View and revoke installer keys created by anyone in the tenant.", TenantOnly),
    new(PermissionNames.AgentInstall, Categories.Agents, "Install agents", "Get the commands that install an agent.", TenantOnly),
  }.ToFrozenDictionary(x => x.Name, StringComparer.Ordinal);

  public static IReadOnlyCollection<PermissionInfo> All => Entries.Values;

  public static PermissionInfo? Find(string name) => Entries.GetValueOrDefault(name);

  public static bool Contains(string name) => Entries.ContainsKey(name);
}
