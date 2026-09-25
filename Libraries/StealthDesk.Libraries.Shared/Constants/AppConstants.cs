using System.Diagnostics;

namespace StealthDesk.Libraries.Shared.Constants;

public static class AppConstants
{
  public const string AgentHubPath = "/hubs/agent";
  public const string DefaultInstanceId = "default";

  public static Uri? ServerUri
  {
    get
    {
      if (OperatingSystem.IsWindows() && Debugger.IsAttached)
      {
        return DevServerUri;
      }

      return null;
    }
  }
  public static int SignalrMaxMessageSize => 30 * 1024; // 30KB.

  private static Uri DevServerUri { get; } = new("http://localhost:5099");
}
