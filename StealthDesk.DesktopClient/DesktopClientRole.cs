using StealthDesk.Branding;
using StealthDesk.Contracts.Agents;

namespace StealthDesk.DesktopClient;

/// <summary>
/// Whether this process is a desktop client the agent started. The agent starts it as LocalSystem inside a user's
/// session, which nobody else can: someone opening it by hand runs it as themselves.
/// </summary>
internal static class DesktopClientRole
{
  /// <summary>Null when the process may run as the desktop client; otherwise what to tell whoever started it.</summary>
  /// <param name="isSystem">Whether the process runs as LocalSystem.</param>
  /// <param name="sessionId">The Windows session the process runs in; 0 holds services, not desktops.</param>
  /// <param name="start">What the command line gave, or null when it lacked something.</param>
  /// <param name="isDebugBuild">A debug build may be started by hand, to develop it.</param>
  public static string? Refusal(bool isSystem, int sessionId, DesktopClientStart? start, bool isDebugBuild)
  {
    var byHand = $"The {Brand.Name} desktop client is started by the {Brand.Name} agent; it can't be opened by hand.";
    if (start is null)
    {
      return byHand;
    }

    if (isDebugBuild)
    {
      return null;
    }

    return isSystem && sessionId != 0 ? null : byHand;
  }
}

/// <summary>Whether this is a debug build, which may be started by hand.</summary>
internal static class DesktopClientBuild
{
#if DEBUG
  public const bool IsDebug = true;
#else
  public const bool IsDebug = false;
#endif
}
