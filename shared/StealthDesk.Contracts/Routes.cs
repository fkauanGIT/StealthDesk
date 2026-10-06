namespace StealthDesk.Contracts;

/// <summary>Paths shared by the server and its clients.</summary>
public static class Routes
{
  public const string AgentGateway = "/hubs/agent";
  public const string Dashboard = "/hubs/dashboard";
  public const string Devices = "/api/v1/devices";
  public static string Device(Guid id) => $"{Devices}/{id}";
  public const string ServerVersion = "/api/internal/version/server";

  /// <summary>ASP.NET Core Identity's account endpoints: /register, /login, /refresh, /manage/info and the rest.</summary>
  public const string Auth = "/api/auth";
  public const string CurrentUser = "/api/auth/me";
  public const string SignOut = "/api/auth/sign-out";
  public const string AuthSettings = "/api/auth/settings";

  /// <summary>The signed-in user's own account.</summary>
  public const string Account = "/api/account";
  public const string AccountProfile = "/api/account/profile";
  public const string AccountPassword = "/api/account/password";
  public const string AccountPasswordSet = "/api/account/password/set";
  public const string AccountPersonalData = "/api/account/personal-data";
  public const string AccountDeletion = "/api/account/delete";
}
