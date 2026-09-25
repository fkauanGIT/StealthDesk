namespace StealthDesk.Web.Server.Options;

/// <summary>
/// Application configuration options for the StealthDesk web server.
/// </summary>
public class AppOptions
{
  /// <summary>
  /// The configuration section key for AppOptions in appsettings.json.
  /// </summary>
  public const string SectionKey = "AppOptions";

  /// <summary>
  /// The maximum allowed difference between the agent's signed timestamp and the server's clock.
  /// </summary>
  /// <remarks>
  /// A captured heartbeat keeps a valid signature forever, so without this check it could be replayed later.
  /// <c>null</c> disables the check.
  /// </remarks>
  public TimeSpan? AgentClockSkewTolerance { get; init; }

  /// <summary>
  /// Allows an agent the server has never seen to register itself on its first heartbeat.
  /// Only permitted on single-tenant servers.
  /// </summary>
  public bool AllowAgentsToSelfBootstrap { get; init; }
}
