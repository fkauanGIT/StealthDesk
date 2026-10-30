using StealthDesk.Contracts.Agents;
using StealthDesk.Contracts.TenantSettings;

namespace StealthDesk.Web.Server.TenantSettings;

/// <summary>What every tenant gets until it sets a value of its own. Bound from the <c>TenantDefaults</c> section.</summary>
public sealed class TenantDefaultsOptions
{
  public const string Section = "TenantDefaults";

  public bool AppendInstanceName { get; set; }

  /// <summary>None makes one from the server's address.</summary>
  public string? InstanceName { get; set; }

  public bool NotifyUserOnRemoteControl { get; set; } = true;
}

/// <summary>A value checked and written the one way it's stored, or why it can't be.</summary>
public sealed record NormalizedSetting(string? Value, string? Error)
{
  public bool Succeeded => Error is null;
}

/// <summary>Which settings exist, what each accepts, and the server's default for each.</summary>
public static class TenantSettingRules
{
  public static bool Exists(string name) => TenantSettingNames.All.Contains(name, StringComparer.Ordinal);

  public static NormalizedSetting Normalize(string name, string value)
  {
    var trimmed = value.Trim();
    if (trimmed.Length > TenantSettingNames.ValueMax)
    {
      return new(null, $"A value can't be longer than {TenantSettingNames.ValueMax} characters.");
    }

    return name switch
    {
      TenantSettingNames.AppendInstanceName or TenantSettingNames.NotifyUserOnRemoteControl => bool.TryParse(trimmed, out var on)
        ? new(on ? "true" : "false", null)
        : new(null, $"{name} must be true or false."),
      TenantSettingNames.InstanceName => InstanceNames.Validate(trimmed) is { } error
        ? new(null, error)
        : new(trimmed, null),
      _ => new(null, $"There is no setting named '{name}'."),
    };
  }

  public static string? DefaultOf(string name, TenantDefaultsOptions defaults) => name switch
  {
    TenantSettingNames.AppendInstanceName => defaults.AppendInstanceName ? "true" : "false",
    TenantSettingNames.InstanceName => string.IsNullOrWhiteSpace(defaults.InstanceName) ? null : defaults.InstanceName.Trim(),
    TenantSettingNames.NotifyUserOnRemoteControl => defaults.NotifyUserOnRemoteControl ? "true" : "false",
    _ => null,
  };
}
