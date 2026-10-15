namespace StealthDesk.Contracts.TenantSettings;

/// <summary>The settings a tenant may change. Anything else is refused.</summary>
public static class TenantSettingNames
{
  /// <summary><c>true</c> or <c>false</c>: install commands name an agent instance, so agents of several servers can share a machine.</summary>
  public const string AppendInstanceName = "append-instance-name";

  /// <summary>The instance those commands name; when unset, one made from the server's address.</summary>
  public const string InstanceName = "instance-name";

  public static IReadOnlyList<string> All { get; } = [AppendInstanceName, InstanceName];

  public const int ValueMax = 100;
}

/// <summary>One setting as the tenant sees it.</summary>
/// <param name="Value">The tenant's own value; none when it uses the server's.</param>
/// <param name="DefaultValue">What applies without a value of its own, set on the server.</param>
public sealed record TenantSetting(string Name, string? Value, string? DefaultValue);

public sealed class TenantSettingList
{
  public IReadOnlyList<TenantSetting> Items { get; init; } = [];
}

/// <param name="Value">Empty or none goes back to the server's default.</param>
public sealed record SetTenantSettingRequest(string? Value);

/// <summary>Several settings at once: every one is checked before any is saved.</summary>
public sealed record SetTenantSettingsRequest(IReadOnlyDictionary<string, string?> Values);

/// <summary>What install commands use for the tenant acted in, every value resolved.</summary>
/// <param name="InstanceName">The instance commands name; none when they name no instance.</param>
public sealed record DeploymentOptions(string? InstanceName);
