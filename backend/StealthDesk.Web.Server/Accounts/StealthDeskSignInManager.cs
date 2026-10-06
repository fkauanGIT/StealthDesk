using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace StealthDesk.Web.Server.Accounts;

/// <summary>Identity's sign-in, plus recording when each user last signed in and the passkey sign-in setting.</summary>
public sealed class StealthDeskSignInManager(
  UserManager<UserRecord> userManager,
  IHttpContextAccessor contextAccessor,
  IUserClaimsPrincipalFactory<UserRecord> claimsFactory,
  IOptions<IdentityOptions> optionsAccessor,
  ILogger<SignInManager<UserRecord>> logger,
  IAuthenticationSchemeProvider schemes,
  IUserConfirmation<UserRecord> confirmation,
  IOptionsMonitor<AccountOptions> accounts,
  TimeProvider clock)
  : SignInManager<UserRecord>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
  // Every successful sign-in ends here: password, two-factor, passkey or external login, cookie or bearer.
  public override async Task SignInWithClaimsAsync(
    UserRecord user,
    AuthenticationProperties? authenticationProperties,
    IEnumerable<Claim> additionalClaims)
  {
    await base.SignInWithClaimsAsync(user, authenticationProperties, additionalClaims);
    user.LastSignIn = clock.GetUtcNow();
    await UserManager.UpdateAsync(user);
  }

  // Identity's version always signs in for this browser session only; the server setting decides here.
  public override async Task<SignInResult> PasskeySignInAsync(string credentialJson)
  {
    ArgumentException.ThrowIfNullOrEmpty(credentialJson);

    var assertion = await PerformPasskeyAssertionAsync(credentialJson);
    if (!assertion.Succeeded)
    {
      return SignInResult.Failed;
    }

    // Keeps the sign count and authenticator data current, which is how a cloned authenticator gets noticed.
    var updated = await UserManager.AddOrUpdatePasskeyAsync(assertion.User, assertion.Passkey);
    if (!updated.Succeeded)
    {
      return SignInResult.Failed;
    }

    // A passkey already proves possession and, with user verification, a PIN or biometric: no second factor.
    return await SignInOrTwoFactorAsync(assertion.User, accounts.CurrentValue.PersistPasskeySignIn, bypassTwoFactor: true);
  }
}
