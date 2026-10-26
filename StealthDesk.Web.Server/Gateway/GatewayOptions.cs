namespace StealthDesk.Web.Server.Gateway;

/// <summary>Rules the server applies to reports sent by agents. Bound from the <c>Gateway</c> section.</summary>
public sealed class GatewayOptions
{
  public const string Section = "Gateway";

  /// <summary>
  /// How far an agent's signing time may be from the server's clock. A captured report keeps a valid signature
  /// forever, so this is what stops it from being replayed later. <c>null</c> turns the check off.
  /// </summary>
  public TimeSpan? ClockTolerance { get; init; }

  /// <summary>How long the server waits for an agent to answer a call before giving up on it.</summary>
  public TimeSpan AgentCallTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
