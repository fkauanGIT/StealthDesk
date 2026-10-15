using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Client.Ui;

namespace StealthDesk.Web.Client.Layout;

/// <summary>One page of an area, shown as a tab; <see cref="Policy"/> null for any signed-in user.</summary>
public sealed record SectionTab(string Label, string Href, string? Policy);

/// <summary>
/// An area of the app, grouped by what people come to do rather than by what is stored: the menu lists areas, and an
/// area with several pages shows them as tabs.
/// </summary>
public sealed record AppSection(string Label, IconName Icon, IReadOnlyList<SectionTab> Tabs, IReadOnlyList<string>? AlsoCovers = null)
{
  /// <summary>Whether <paramref name="path"/> (relative, without query) is one of this area's pages.</summary>
  public bool Covers(string path) =>
    Tabs.Any(x => Matches(path, x.Href)) || (AlsoCovers ?? []).Any(x => Matches(path, x));

  public SectionTab? TabFor(string path) =>
    Tabs.Where(x => Matches(path, x.Href)).OrderByDescending(x => x.Href.Length).FirstOrDefault();

  // The devices home is the empty path, so it only matches itself; any other tab also matches the pages under it.
  private static bool Matches(string path, string href) =>
    href.Length == 0 ? path.Length == 0 : path == href || path.StartsWith($"{href}/", StringComparison.Ordinal);
}

public static class AppSections
{
  public static IReadOnlyList<AppSection> All { get; } =
  [
    new("Devices", IconName.Device, [new("Devices", string.Empty, null)], AlsoCovers: ["devices"]),
    new("People & access", IconName.Users,
    [
      new("People", "users", For(PermissionNames.TenantUsersRead)),
      new("Groups", "user-groups", For(PermissionNames.TenantUserGroupsRead)),
      new("Invites", "invites", For(PermissionNames.TenantUsersWrite)),
      new("Permissions", "permissions", For(PermissionNames.TenantPermissionsRead)),
    ]),
    new("Integrations", IconName.Key,
    [
      new("Access tokens", "personal-access-tokens", For(PermissionNames.PersonalAccessTokenSelfRead)),
      new("Service accounts", "service-accounts", For(PermissionNames.ServiceAccountRead)),
      new("Installer keys", "installer-keys", For(PermissionNames.InstallerKeyRead)),
    ]),
    new("Activity", IconName.History, [new("Activity", "authorization-logs", For(PermissionNames.TenantAuthorizationLogsRead))]),
    new("Settings", IconName.Settings, [new("Tenant", "settings", For(PermissionNames.TenantSettingsRead))]),
    new("Server", IconName.Shield,
    [
      new("Tenants", "server/tenants", For(PermissionNames.ServerTenantsRead)),
      new("Server settings", "server/settings", For(PermissionNames.ServerSettingsWrite)),
      new("Server accounts", "server/service-accounts", For(PermissionNames.ServerServiceAccountsRead)),
      new("Server activity", "server/authorization-logs", For(PermissionNames.ServerAuthorizationLogsRead)),
    ]),
  ];

  /// <summary>The area <paramref name="path"/> belongs to, if any.</summary>
  public static AppSection? For(string path, IEnumerable<AppSection> sections) =>
    sections.FirstOrDefault(x => x.Covers(path));

  private static string For(string permission) => PermissionPolicies.For(permission);
}
