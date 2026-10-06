namespace StealthDesk.Web.Client.Accounts;

/// <summary>Places Identity's error codes next to the field they are about.</summary>
public static class FieldErrors
{
  public static string? ForPassword(AccountResult result) => Join(result, IsPassword);

  /// <summary>The password the user typed to prove it's them, not the new one.</summary>
  public static string? ForCurrentPassword(AccountResult result) => Join(result, IsCurrentPassword);

  public static string? ForEmail(AccountResult result) => Join(result, IsEmail);

  public static string? ForPhoneNumber(AccountResult result) => Join(result, IsPhoneNumber);

  /// <summary>What no field shows, for the alert above the form.</summary>
  public static string? Remaining(AccountResult result) =>
    result.FieldErrors is { Count: > 0 }
      ? Join(result, code => !IsPassword(code) && !IsCurrentPassword(code) && !IsEmail(code) && !IsPhoneNumber(code))
      : result.Error;

  private static bool IsPassword(string code) => code.StartsWith("Password", StringComparison.Ordinal) && !IsCurrentPassword(code);

  private static bool IsCurrentPassword(string code) => code == "PasswordMismatch";

  private static bool IsPhoneNumber(string code) => code == "InvalidPhoneNumber";

  private static bool IsEmail(string code) => code is "DuplicateUserName" or "DuplicateEmail" or "InvalidEmail" or "InvalidUserName";

  private static string? Join(AccountResult result, Func<string, bool> belongs)
  {
    var messages = result.FieldErrors?.Where(x => belongs(x.Key)).SelectMany(x => x.Value).ToList() ?? [];
    return messages.Count == 0 ? null : string.Join(" ", messages);
  }
}
