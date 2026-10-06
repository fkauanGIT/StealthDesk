using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Accounts;

namespace StealthDesk.Web.Client.Accounts;

/// <summary>What an account call answered: success, or why not, with errors per field when there are any.</summary>
public sealed record AccountResult(bool Succeeded, string? Error = null, IReadOnlyDictionary<string, string[]>? FieldErrors = null)
{
  public static readonly AccountResult Success = new(true);

  /// <summary>Identity's sign-in outcome when it isn't a plain wrong password.</summary>
  public string? Detail { get; init; }

  public bool IsLockedOut => Detail == "LockedOut";

  /// <summary>Signing in is refused until the email is confirmed.</summary>
  public bool IsNotAllowed => Detail == "NotAllowed";

  /// <summary>The password was right; the account also needs a code from the authenticator app.</summary>
  public bool IsTwoFactorRequired => Detail == "RequiresTwoFactor";
}

/// <summary>The server's account endpoints, as the pages use them. The browser carries the session cookie.</summary>
public sealed class AccountApi(HttpClient http)
{
  private static readonly AccountResult Unreachable = new(false, "The server can't be reached. Try again in a moment.");

  public async Task<CurrentUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default)
  {
    using var response = await http.GetAsync(Routes.CurrentUser, cancellationToken);
    return response.StatusCode == HttpStatusCode.OK
      ? await response.Content.ReadFromJsonAsync<CurrentUser>(cancellationToken)
      : null;
  }

  public async Task<AccountSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
    await http.GetFromJsonAsync<AccountSettings>(Routes.AuthSettings, cancellationToken) ?? new AccountSettings();

  /// <param name="remember">Keep the session after the browser closes; otherwise it lasts until then.</param>
  public Task<AccountResult> SignInAsync(string email, string password, bool remember) =>
    PostAsync($"{Routes.Auth}/login?{(remember ? "useCookies" : "useSessionCookies")}=true", new { email, password });

  public Task<AccountResult> RegisterAsync(string email, string password) =>
    PostAsync($"{Routes.Auth}/register", new { email, password });

  public Task<AccountResult> SignOutAsync() => PostAsync(Routes.SignOut, new { });

  public Task<AccountResult> ForgotPasswordAsync(string email) =>
    PostAsync($"{Routes.Auth}/forgotPassword", new { email });

  public Task<AccountResult> ResetPasswordAsync(string email, string resetCode, string newPassword) =>
    PostAsync($"{Routes.Auth}/resetPassword", new { email, resetCode, newPassword });

  public Task<AccountResult> ResendConfirmationAsync(string email) =>
    PostAsync($"{Routes.Auth}/resendConfirmationEmail", new { email });

  /// <summary>The signed-in user's account, or null when the session is gone.</summary>
  public async Task<AccountProfile?> GetProfileAsync(CancellationToken cancellationToken = default) =>
    await GetOrNullAsync<AccountProfile>(Routes.AccountProfile, cancellationToken);

  public Task<AccountResult> UpdateProfileAsync(string? phoneNumber) =>
    SendAsync(HttpMethod.Put, Routes.AccountProfile, new ProfileUpdate { PhoneNumber = phoneNumber });

  public Task<AccountResult> ChangePasswordAsync(string currentPassword, string newPassword) =>
    PostAsync(Routes.AccountPassword, new PasswordChange { CurrentPassword = currentPassword, NewPassword = newPassword });

  /// <summary>For an account that only signs in with an external login.</summary>
  public Task<AccountResult> SetPasswordAsync(string newPassword) =>
    PostAsync(Routes.AccountPasswordSet, new PasswordSet { NewPassword = newPassword });

  /// <summary>Sends a link to the new address; the email changes once it is opened.</summary>
  public Task<AccountResult> ChangeEmailAsync(string newEmail) =>
    PostAsync($"{Routes.Auth}/manage/info", new { newEmail });

  public Task<AccountResult> DeleteAccountAsync(string? password) =>
    PostAsync(Routes.AccountDeletion, new AccountDeletion { Password = password });

  public async Task<TwoFactorStatus?> GetTwoFactorAsync(CancellationToken cancellationToken = default) =>
    await GetOrNullAsync<TwoFactorStatus>(Routes.TwoFactor, cancellationToken);

  /// <summary>The key and QR code for the authenticator app; the server creates the key the first time.</summary>
  public async Task<AuthenticatorSetup?> GetAuthenticatorAsync(CancellationToken cancellationToken = default) =>
    await GetOrNullAsync<AuthenticatorSetup>(Routes.Authenticator, cancellationToken);

  /// <summary>Turns two-factor on once the app's code checks out. New recovery codes come back on a first setup.</summary>
  public Task<(AccountResult Result, RecoveryCodeSet? Codes)> EnableTwoFactorAsync(string code) =>
    PostForAsync<RecoveryCodeSet>(Routes.TwoFactorEnable, new TwoFactorEnable { Code = code });

  public Task<AccountResult> DisableTwoFactorAsync() => PostAsync(Routes.TwoFactorDisable, new { });

  public Task<AccountResult> ResetAuthenticatorAsync() => PostAsync(Routes.AuthenticatorReset, new { });

  public Task<(AccountResult Result, RecoveryCodeSet? Codes)> GenerateRecoveryCodesAsync() =>
    PostForAsync<RecoveryCodeSet>(Routes.RecoveryCodes, new { });

  public Task<AccountResult> ForgetBrowserAsync() => PostAsync(Routes.ForgetBrowser, new { });

  /// <summary>The second sign-in step, in the same browser that passed the password.</summary>
  public Task<AccountResult> SignInWithCodeAsync(string code, bool rememberMe, bool rememberBrowser) =>
    PostAsync(Routes.SignInTwoFactor, new TwoFactorSignIn { Code = code, RememberMe = rememberMe, RememberBrowser = rememberBrowser });

  public Task<AccountResult> SignInWithRecoveryCodeAsync(string recoveryCode) =>
    PostAsync(Routes.SignInRecoveryCode, new RecoveryCodeSignIn { RecoveryCode = recoveryCode });

  /// <summary>The user's passkeys, or null when the session is gone.</summary>
  public async Task<IReadOnlyList<PasskeySummary>?> GetPasskeysAsync(CancellationToken cancellationToken = default) =>
    await GetOrNullAsync<List<PasskeySummary>>(Routes.Passkeys, cancellationToken);

  /// <summary>The options, with a fresh challenge, that the browser hands to the authenticator to create a passkey.</summary>
  public Task<(AccountResult Result, string? Json)> GetPasskeyCreationOptionsAsync() => PostForTextAsync(Routes.PasskeyCreationOptions);

  public Task<(AccountResult Result, PasskeySummary? Passkey)> AddPasskeyAsync(string credentialJson) =>
    PostForAsync<PasskeySummary>(Routes.Passkeys, new PasskeyCredential { CredentialJson = credentialJson });

  public Task<AccountResult> RenamePasskeyAsync(string id, string name) =>
    SendAsync(HttpMethod.Put, Routes.Passkey(id), new PasskeyRename { Name = name });

  public async Task<AccountResult> RemovePasskeyAsync(string id)
  {
    try
    {
      using var response = await http.DeleteAsync(Routes.Passkey(id));
      return response.IsSuccessStatusCode ? AccountResult.Success : await FailureAsync(response);
    }
    catch (HttpRequestException)
    {
      return Unreachable;
    }
  }

  /// <param name="email">Names that user's passkeys; without it, the browser offers any passkey it has for this site.</param>
  public Task<(AccountResult Result, string? Json)> GetPasskeyRequestOptionsAsync(string? email = null) =>
    PostForTextAsync(string.IsNullOrWhiteSpace(email)
      ? Routes.PasskeyRequestOptions
      : $"{Routes.PasskeyRequestOptions}?email={Uri.EscapeDataString(email.Trim())}");

  public Task<AccountResult> SignInWithPasskeyAsync(string credentialJson) =>
    PostAsync(Routes.SignInPasskey, new PasskeyCredential { CredentialJson = credentialJson });

  private async Task<(AccountResult, string?)> PostForTextAsync(string path)
  {
    try
    {
      using var response = await http.PostAsync(path, null);
      return response.IsSuccessStatusCode
        ? (AccountResult.Success, await response.Content.ReadAsStringAsync())
        : (await FailureAsync(response), null);
    }
    catch (HttpRequestException)
    {
      return (Unreachable, null);
    }
  }

  private async Task<T?> GetOrNullAsync<T>(string path, CancellationToken cancellationToken)
  {
    using var response = await http.GetAsync(path, cancellationToken);
    return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<T>(cancellationToken) : default;
  }

  private async Task<(AccountResult, T?)> PostForAsync<T>(string path, object body)
  {
    try
    {
      using var response = await http.PostAsJsonAsync(path, body);
      return response.IsSuccessStatusCode
        ? (AccountResult.Success, await response.Content.ReadFromJsonAsync<T>())
        : (await FailureAsync(response), default);
    }
    catch (HttpRequestException)
    {
      return (Unreachable, default);
    }
  }

  private Task<AccountResult> PostAsync(string path, object body) => SendAsync(HttpMethod.Post, path, body);

  private async Task<AccountResult> SendAsync(HttpMethod method, string path, object body)
  {
    try
    {
      using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
      using var response = await http.SendAsync(request);
      return response.IsSuccessStatusCode ? AccountResult.Success : await FailureAsync(response);
    }
    catch (HttpRequestException)
    {
      return Unreachable;
    }
  }

  // Identity answers with problem details: a "detail" for sign-in outcomes, "errors" for validation.
  private static async Task<AccountResult> FailureAsync(HttpResponseMessage response)
  {
    string? detail = null;
    Dictionary<string, string[]>? errors = null;
    try
    {
      var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
      if (problem.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
      {
        detail = d.GetString();
      }

      if (problem.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
      {
        errors = e.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.EnumerateArray().Select(v => v.GetString() ?? string.Empty).ToArray());
      }
    }
    catch (JsonException)
    {
      // Not every failure has a body.
    }

    var message = response.StatusCode switch
    {
      HttpStatusCode.NotFound => "This isn't available on this server.",
      HttpStatusCode.Unauthorized => detail switch
      {
        "LockedOut" => "This account is locked after too many attempts. Try again in a few minutes.",
        "NotAllowed" => "Confirm your email address before signing in.",
        "RequiresTwoFactor" => "This account needs a two-factor code.",
        _ => "Email or password is wrong.",
      },
      _ when errors is { Count: > 0 } => errors.Values.SelectMany(x => x).FirstOrDefault(),
      _ => detail ?? "Something went wrong. Try again.",
    };

    return new AccountResult(false, message, errors) { Detail = detail };
  }
}
