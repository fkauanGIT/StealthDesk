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
  public const string TwoFactor = "/api/account/two-factor";
  public const string TwoFactorEnable = "/api/account/two-factor/enable";
  public const string TwoFactorDisable = "/api/account/two-factor/disable";
  public const string Authenticator = "/api/account/two-factor/authenticator";
  public const string AuthenticatorReset = "/api/account/two-factor/authenticator/reset";
  public const string RecoveryCodes = "/api/account/two-factor/recovery-codes";
  public const string ForgetBrowser = "/api/account/two-factor/forget-browser";

  /// <summary>The second sign-in step, after /login answered that the account needs two-factor.</summary>
  public const string SignInTwoFactor = "/api/auth/two-factor";
  public const string SignInRecoveryCode = "/api/auth/recovery-code";
}
