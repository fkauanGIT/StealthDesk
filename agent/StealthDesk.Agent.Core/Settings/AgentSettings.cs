using System.Text.Json.Serialization;

namespace StealthDesk.Agent.Core.Settings;

/// <summary>Who this agent is and where it reports to. Bound from the <c>Agent</c> section.</summary>
public sealed record AgentSettings
{
  public const string Section = "Agent";

  /// <summary>Empty until the server assigns one with the first accepted report.</summary>
  public Guid DeviceId { get; init; }

  /// <summary>Empty until the device belongs to a tenant.</summary>
  public Guid TenantId { get; init; }

  public Uri? ServerUrl { get; init; }

  /// <summary>Ed25519 private key (base64) created on first run. It is the device's identity: never log or share it.</summary>
  public string? PrivateKey { get; init; }

  /// <summary>
  /// <c>{id}:{secret}</c> of the installer key that lets this device join, from the command line or the shipped
  /// settings. Sent until the server accepts a report, and never written to the agent's own settings file.
  /// </summary>
  [JsonIgnore]
  public string? InstallerKey { get; init; }
}
