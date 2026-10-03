using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace StealthDesk.Web.Server.Accounts;

/// <summary>Identity's sign-in, plus recording when each user last signed in.</summary>
public sealed class StealthDeskSignInManager(
  UserManager<UserRecord> userManager,
  IHttpContextAccessor contextAccessor,
  IUserClaimsPrincipalFactory<UserRecord> claimsFactory,
  IOptions<IdentityOptions> optionsAccessor,
  ILogger<SignInManager<UserRecord>> logger,
  IAuthenticationSchemeProvider schemes,
  IUserConfirmation<UserRecord> confirmation,
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
}
