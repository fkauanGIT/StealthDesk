using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using QRCoder;
using StealthDesk.Branding;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.Accounts;

/// <summary>
/// Two-factor authentication with an authenticator app: setting it up, recovery codes, and the second sign-in step.
/// Turning it on, off or resetting the key changes the security stamp; like a password change, this session keeps a
/// renewed cookie and the other sessions sign out.
/// </summary>
public static class TwoFactorEndpoints
{
  private const int RecoveryCodeCount = 10;

  public static IEndpointRouteBuilder MapTwoFactorEndpoints(this IEndpointRouteBuilder endpoints)
  {
    var manage = endpoints.MapGroup(Routes.TwoFactor).RequireAuthorization().OwnAccount();

    manage.MapGet("/", async (ClaimsPrincipal principal, SignInManager<UserRecord> signIn) =>
    {
      var users = signIn.UserManager;
      if (await users.GetUserAsync(principal) is not { } user)
      {
        return Results.Unauthorized();
      }

      return Results.Ok(new TwoFactorStatus
      {
        Enabled = user.TwoFactorEnabled,
        HasAuthenticator = await users.GetAuthenticatorKeyAsync(user) is not null,
        RecoveryCodesLeft = await users.CountRecoveryCodesAsync(user),
        IsBrowserRemembered = await signIn.IsTwoFactorClientRememberedAsync(user),
      });
    });

    // Creates the key on first use, so the page always has one to show.
    manage.MapGet("/authenticator", async (HttpContext context, SignInManager<UserRecord> signIn) =>
    {
      var users = signIn.UserManager;
      if (await users.GetUserAsync(context.User) is not { } user)
      {
        return Results.Unauthorized();
      }

      var key = await users.GetAuthenticatorKeyAsync(user);
      if (string.IsNullOrEmpty(key))
      {
        // Creating a key also changes the security stamp, which would end this session.
        await users.ResetAuthenticatorKeyAsync(user);
        await ManageEndpoints.RefreshCookieAsync(context, signIn, user);
        key = await users.GetAuthenticatorKeyAsync(user) ?? string.Empty;
      }

      var uri = AuthenticatorUri(user.Email ?? string.Empty, key);
      return Results.Ok(new AuthenticatorSetup
      {
        SharedKey = InGroupsOfFour(key),
        AuthenticatorUri = uri,
        QrCode = "data:image/png;base64," + Convert.ToBase64String(PngByteQRCodeHelper.GetQRCode(uri, QRCodeGenerator.ECCLevel.Q, 5)),
      });
    });

    manage.MapPost("/enable", async (TwoFactorEnable enable, HttpContext context, SignInManager<UserRecord> signIn, ILogger<UserRecord> logger) =>
    {
      var users = signIn.UserManager;
      if (await users.GetUserAsync(context.User) is not { } user)
      {
        return Results.Unauthorized();
      }

      var valid = await users.GetAuthenticatorKeyAsync(user) is not null
        && await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, Normalize(enable.Code));
      if (!valid)
      {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["InvalidCode"] = ["The verification code is wrong. Check the app and try again."] });
      }

      await users.SetTwoFactorEnabledAsync(user, true);
      await ManageEndpoints.RefreshCookieAsync(context, signIn, user);
      logger.LogInformation("User {UserId} turned on two-factor authentication.", user.Id);

      // Codes from an earlier setup stay valid; only a first setup gets new ones.
      var codes = await users.CountRecoveryCodesAsync(user) == 0
        ? await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount) ?? []
        : [];
      return Results.Ok(new RecoveryCodeSet { Codes = [.. codes] });
    });

    manage.MapPost("/disable", async (HttpContext context, SignInManager<UserRecord> signIn, ILogger<UserRecord> logger) =>
    {
      var users = signIn.UserManager;
      if (await users.GetUserAsync(context.User) is not { } user)
      {
        return Results.Unauthorized();
      }

      var result = await users.SetTwoFactorEnabledAsync(user, false);
      if (!result.Succeeded)
      {
        return ManageEndpoints.ValidationProblem(result);
      }

      await ManageEndpoints.RefreshCookieAsync(context, signIn, user);
      logger.LogInformation("User {UserId} turned off two-factor authentication.", user.Id);
      return Results.NoContent();
    });

    // A new key makes the old app entries useless, so two-factor is off until the app is set up again.
    manage.MapPost("/authenticator/reset", async (HttpContext context, SignInManager<UserRecord> signIn, ILogger<UserRecord> logger) =>
    {
      var users = signIn.UserManager;
      if (await users.GetUserAsync(context.User) is not { } user)
      {
        return Results.Unauthorized();
      }

      await users.SetTwoFactorEnabledAsync(user, false);
      await users.ResetAuthenticatorKeyAsync(user);
      await ManageEndpoints.RefreshCookieAsync(context, signIn, user);
      logger.LogInformation("User {UserId} reset their authenticator key.", user.Id);
      return Results.NoContent();
    });

    manage.MapPost("/recovery-codes", async (ClaimsPrincipal principal, UserManager<UserRecord> users, ILogger<UserRecord> logger) =>
    {
      if (await users.GetUserAsync(principal) is not { } user)
      {
        return Results.Unauthorized();
      }

      if (!user.TwoFactorEnabled)
      {
        return Results.Problem("Turn on two-factor authentication before generating recovery codes.", statusCode: StatusCodes.Status400BadRequest);
      }

      var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount) ?? [];
      logger.LogInformation("User {UserId} generated new recovery codes.", user.Id);
      return Results.Ok(new RecoveryCodeSet { Codes = [.. codes] });
    });

    manage.MapPost("/forget-browser", async (SignInManager<UserRecord> signIn) =>
    {
      await signIn.ForgetTwoFactorClientAsync();
      return Results.NoContent();
    });

    // The first step, /login, left a short-lived cookie saying who passed the password; these finish the sign-in.
    var signInStep = endpoints.MapGroup(Routes.Auth).AllowAnonymous();

    signInStep.MapPost("/two-factor", async (TwoFactorSignIn request, SignInManager<UserRecord> signIn) =>
      Outcome(await signIn.TwoFactorAuthenticatorSignInAsync(Normalize(request.Code), request.RememberMe, request.RememberBrowser)));

    signInStep.MapPost("/recovery-code", async (RecoveryCodeSignIn request, SignInManager<UserRecord> signIn) =>
      Outcome(await signIn.TwoFactorRecoveryCodeSignInAsync(request.RecoveryCode.Replace(" ", string.Empty))));

    return endpoints;
  }

  // Answers like Identity's /login: the outcome's name in the problem's detail.
  private static IResult Outcome(SignInResult result) =>
    result.Succeeded ? Results.Ok() : Results.Problem(result.ToString(), statusCode: StatusCodes.Status401Unauthorized);

  // Apps show codes as "123 456" or "123-456". Recovery codes keep their dash: Identity compares them as stored.
  private static string Normalize(string code) => code.Replace(" ", string.Empty).Replace("-", string.Empty);

  private static string AuthenticatorUri(string email, string key) =>
    $"otpauth://totp/{Uri.EscapeDataString(Brand.Name)}:{Uri.EscapeDataString(email)}"
    + $"?secret={key}&issuer={Uri.EscapeDataString(Brand.Name)}&digits=6";

  private static string InGroupsOfFour(string key)
  {
    var text = new StringBuilder();
    for (var i = 0; i < key.Length; i += 4)
    {
      text.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
    }

    return text.ToString().TrimEnd().ToLowerInvariant();
  }
}
