namespace StealthDesk.Contracts;

/// <summary>Paths shared by the server and its clients.</summary>
public static class Routes
{
  public const string AgentGateway = "/hubs/agent";
  public const string Dashboard = "/hubs/dashboard";
  public const string Devices = "/api/v1/devices";
  public const string ServerVersion = "/api/internal/version/server";
}
