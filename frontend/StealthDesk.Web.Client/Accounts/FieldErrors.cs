namespace StealthDesk.Web.Client.Accounts;

/// <summary>Places Identity's error codes next to the field they are about.</summary>
public static class FieldErrors
{
  public static string? ForPassword(AccountResult result) => Join(result, IsPassword);

  public static string? ForEmail(AccountResult result) => Join(result, IsEmail);

  /// <summary>What no field shows, for the alert above the form.</summary>
  public static string? Remaining(AccountResult result) =>
    result.FieldErrors is { Count: > 0 } errors ? Join(result, code => !IsPassword(code) && !IsEmail(code)) : result.Error;

  private static bool IsPassword(string code) => code.StartsWith("Password", StringComparison.Ordinal);

  private static bool IsEmail(string code) => code is "DuplicateUserName" or "DuplicateEmail" or "InvalidEmail" or "InvalidUserName";

  private static string? Join(AccountResult result, Func<string, bool> belongs)
  {
    var messages = result.FieldErrors?.Where(x => belongs(x.Key)).SelectMany(x => x.Value).ToList() ?? [];
    return messages.Count == 0 ? null : string.Join(" ", messages);
  }
}
