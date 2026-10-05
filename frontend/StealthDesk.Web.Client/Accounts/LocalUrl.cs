namespace StealthDesk.Web.Client.Accounts;

public static class LocalUrl
{
  /// <summary>A return address only if it stays on this site, so a crafted link can't send users elsewhere.</summary>
  public static string Or(string? returnUrl, string fallback = "") =>
    returnUrl is { Length: > 0 } && returnUrl[0] == '/' && !returnUrl.StartsWith("//", StringComparison.Ordinal)
      && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
      ? returnUrl.TrimStart('/')
      : fallback;
}
