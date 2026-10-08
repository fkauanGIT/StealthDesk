namespace StealthDesk.Contracts.Permissions;

/// <summary>
/// Every permission is also an authorization policy, named after it, so endpoints and hub methods require a
/// permission by name: <c>RequireAuthorization(PermissionPolicies.For(PermissionNames.DeviceRead))</c>.
/// </summary>
public static class PermissionPolicies
{
  public const string Prefix = "permission:";

  public static string For(string permission) => Prefix + permission;

  /// <summary>The permission a policy name stands for, or null when it isn't a permission policy.</summary>
  public static string? PermissionOf(string policy) =>
    policy.StartsWith(Prefix, StringComparison.Ordinal) ? policy[Prefix.Length..] : null;
}
