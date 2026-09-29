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

  /// <summary>
  /// Lets an agent the server has never seen register itself with its first report.
  /// Only works while the server has a single tenant.
  /// </summary>
  public bool AllowSelfRegistration { get; init; }
}
