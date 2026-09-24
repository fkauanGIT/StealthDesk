namespace StealthDesk.Libraries.Shared.Constants;

public static class AppConstants
{
  public const string AgentHubPath = "/hubs/agent";

  public static int SignalrMaxMessageSize => 30 * 1024; // 30KB.
}
