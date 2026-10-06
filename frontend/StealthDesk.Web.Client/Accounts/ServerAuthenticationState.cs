using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using StealthDesk.Contracts.Accounts;

namespace StealthDesk.Web.Client.Accounts;

/// <summary>
/// Who is signed in, asked from the server: the session lives in an HTTP-only cookie the browser code can't read.
/// The answer is kept until something signs in, signs out or finds the session expired.
/// </summary>
public sealed class ServerAuthenticationState(AccountApi api) : AuthenticationStateProvider
{
  public const string TenantIdClaim = "stealthdesk:tenant_id";
  public const string ServerAdministratorClaim = "stealthdesk:server_admin";
  public const string TenantAdministratorClaim = "stealthdesk:tenant_admin";
  public const string MustChangePasswordClaim = "stealthdesk:must_change_password";

  private static readonly AuthenticationState SignedOut = new(new ClaimsPrincipal(new ClaimsIdentity()));

  private Task<AuthenticationState>? _state;

  public override Task<AuthenticationState> GetAuthenticationStateAsync() => _state ??= LoadAsync();

  /// <summary>Asks the server again, after signing in or out.</summary>
  public void Refresh()
  {
    _state = LoadAsync();
    NotifyAuthenticationStateChanged(_state);
  }

  /// <summary>The server refused the session: treat the user as signed out without asking.</summary>
  public void MarkSignedOut()
  {
    _state = Task.FromResult(SignedOut);
    NotifyAuthenticationStateChanged(_state);
  }

  private async Task<AuthenticationState> LoadAsync()
  {
    try
    {
      return await api.GetCurrentUserAsync() is { } user ? new AuthenticationState(Principal(user)) : SignedOut;
    }
    catch (HttpRequestException)
    {
      return SignedOut;
    }
  }

  private static ClaimsPrincipal Principal(CurrentUser user)
  {
    var claims = new List<Claim>
    {
      new(ClaimTypes.NameIdentifier, user.Id.ToString()),
      new(ClaimTypes.Name, user.Email),
      new(ClaimTypes.Email, user.Email),
      new(TenantIdClaim, user.TenantId.ToString()),
    };

    if (user.IsServerAdministrator)
    {
      claims.Add(new Claim(ServerAdministratorClaim, "true"));
    }

    if (user.IsTenantAdministrator)
    {
      claims.Add(new Claim(TenantAdministratorClaim, "true"));
    }

    if (user.MustChangePassword)
    {
      claims.Add(new Claim(MustChangePasswordClaim, "true"));
    }

    return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "StealthDesk"));
  }
}

/// <summary>Sends the user to sign in when the server says their session is gone, then back to where they were.</summary>
public sealed class SessionGuard(ServerAuthenticationState state, NavigationManager navigation)
{
  public void Expired()
  {
    state.MarkSignedOut();
    navigation.NavigateTo(SignInPath(navigation));
  }

  public static string SignInPath(NavigationManager navigation)
  {
    var returnUrl = navigation.ToBaseRelativePath(navigation.Uri);
    var signInPage = returnUrl.StartsWith("account/", StringComparison.Ordinal) && !returnUrl.StartsWith("account/manage", StringComparison.Ordinal);
    return returnUrl.Length == 0 || signInPage
      ? "account/sign-in"
      : $"account/sign-in?returnUrl={Uri.EscapeDataString("/" + returnUrl)}";
  }
}
