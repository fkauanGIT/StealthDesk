using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.Accounts;

/// <summary>
/// Signing in with Microsoft or GitHub, and linking them to an account. The browser leaves for the provider and comes
/// back to a callback here, which answers with a redirect to a page of the web client.
/// </summary>
public static class ExternalLoginEndpoints
{
  public static IEndpointRouteBuilder MapExternalLoginEndpoints(this IEndpointRouteBuilder endpoints)
  {
    var external = endpoints.MapGroup("/api/auth/external").AllowAnonymous();

    external.MapGet("/{provider}", async (string provider, string? returnUrl, HttpContext context, SignInManager<UserRecord> signIn) =>
    {
      if (!await IsConfiguredAsync(signIn, provider))
      {
        return Results.NotFound();
      }

      // A login left over from an earlier attempt must not be mistaken for this one.
      await context.SignOutAsync(IdentityConstants.ExternalScheme);
      var callback = $"{Routes.ExternalCallback}?returnUrl={Uri.EscapeDataString(LocalPath(returnUrl))}";
      return Results.Challenge(signIn.ConfigureExternalAuthenticationProperties(provider, callback), [provider]);
    });

    external.MapGet("/callback", async (string? returnUrl, SignInManager<UserRecord> signIn) =>
    {
      var info = await signIn.GetExternalLoginInfoAsync();
      if (info is null)
      {
        return Results.Redirect("/account/sign-in?external=failed");
      }

      var back = LocalPath(returnUrl);
      // The provider is trusted with the second factor: Microsoft and GitHub have their own two-step verification.
      var result = await signIn.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: true);
      if (result.Succeeded)
      {
        return Results.Redirect(back);
      }

      if (result.IsLockedOut)
      {
        return Results.Redirect("/account/locked-out");
      }

      if (result.IsNotAllowed)
      {
        return Results.Redirect("/account/sign-in?external=unconfirmed");
      }

      // No account has this login yet: the page offers to create one, keeping the provider's answer in its cookie.
      return Results.Redirect($"/account/external-login?returnUrl={Uri.EscapeDataString(back)}");
    });

    external.MapGet("/pending", async (SignInManager<UserRecord> signIn) =>
      await signIn.GetExternalLoginInfoAsync() is { } info
        ? Results.Ok(new PendingExternalLogin
        {
          ProviderDisplayName = info.ProviderDisplayName ?? info.LoginProvider,
          Email = info.Principal.FindFirstValue(ClaimTypes.Email),
        })
        : Results.NotFound());

    external.MapPost("/register", async (ExternalRegistration request, HttpContext context, SignInManager<UserRecord> signIn, IRegistration registration) =>
    {
      if (await signIn.GetExternalLoginInfoAsync() is not { } info)
      {
        return Results.Problem("The sign-in with the provider expired. Start again.", statusCode: StatusCodes.Status400BadRequest);
      }

      var email = request.Email.Trim();
      var result = await registration.RegisterExternalAsync(email, info, context.RequestAborted);
      if (result.WasClosed)
      {
        return Results.NotFound();
      }

      if (!result.Succeeded)
      {
        return ManageEndpoints.ValidationProblem(result.Identity);
      }

      await context.SignOutAsync(IdentityConstants.ExternalScheme);
      var user = result.User!;
      if (signIn.Options.SignIn.RequireConfirmedEmail && !user.EmailConfirmed)
      {
        return Results.Ok(new ExternalRegistrationResult { SignedIn = false });
      }

      await signIn.SignInAsync(user, isPersistent: false, info.LoginProvider);
      return Results.Ok(new ExternalRegistrationResult { SignedIn = true });
    });

    var logins = endpoints.MapGroup(Routes.Logins).RequireAuthorization().OwnAccount();

    logins.MapGet("/", async (ClaimsPrincipal principal, SignInManager<UserRecord> signIn) =>
    {
      var users = signIn.UserManager;
      if (await users.GetUserAsync(principal) is not { } user)
      {
        return Results.Unauthorized();
      }

      var linked = await users.GetLoginsAsync(user);
      var providers = await ProvidersAsync(signIn);
      return Results.Ok(new LinkedLogins
      {
        Linked = [.. linked.Select(x => new LinkedLogin
        {
          Provider = x.LoginProvider,
          DisplayName = x.ProviderDisplayName ?? x.LoginProvider,
          ProviderKey = x.ProviderKey,
        })],
        Available = [.. providers.Where(x => linked.All(login => login.LoginProvider != x.Scheme))],
        CanRemove = linked.Count > 1 || await HasOtherWayToSignInAsync(users, user),
      });
    });

    logins.MapGet("/link/{provider}", async (string provider, HttpContext context, SignInManager<UserRecord> signIn) =>
    {
      if (!await IsConfiguredAsync(signIn, provider))
      {
        return Results.NotFound();
      }

      await context.SignOutAsync(IdentityConstants.ExternalScheme);

      // The user ID in the properties ties the provider's answer to this account.
      var properties = signIn.ConfigureExternalAuthenticationProperties(provider, Routes.LinkLoginCallback, signIn.UserManager.GetUserId(context.User));
      return Results.Challenge(properties, [provider]);
    });

    logins.MapGet("/link-callback", async (HttpContext context, SignInManager<UserRecord> signIn, ILogger<UserRecord> logger) =>
    {
      var users = signIn.UserManager;
      if (await users.GetUserAsync(context.User) is not { } user)
      {
        return Results.Redirect("/account/sign-in");
      }

      var info = await signIn.GetExternalLoginInfoAsync(user.Id.ToString());
      if (info is null)
      {
        return Results.Redirect("/account/manage/external-logins?error=failed");
      }

      var added = await users.AddLoginAsync(user, info);
      await context.SignOutAsync(IdentityConstants.ExternalScheme);
      if (!added.Succeeded)
      {
        var taken = added.Errors.Any(x => x.Code == nameof(IdentityErrorDescriber.LoginAlreadyAssociated));
        return Results.Redirect($"/account/manage/external-logins?error={(taken ? "taken" : "failed")}");
      }

      logger.LogInformation("User {UserId} linked a {Provider} login.", user.Id, info.LoginProvider);
      return Results.Redirect("/account/manage/external-logins?done=linked");
    });

    logins.MapDelete("/{provider}/{providerKey}", async (string provider, string providerKey, HttpContext context, SignInManager<UserRecord> signIn, ILogger<UserRecord> logger) =>
    {
      var users = signIn.UserManager;
      if (await users.GetUserAsync(context.User) is not { } user)
      {
        return Results.Unauthorized();
      }

      var linked = await users.GetLoginsAsync(user);
      if (!linked.Any(x => x.LoginProvider == provider && x.ProviderKey == providerKey))
      {
        return Results.NotFound();
      }

      if (linked.Count == 1 && !await HasOtherWayToSignInAsync(users, user))
      {
        return Results.Problem(
          "This is the only way to sign in to this account. Set a password or add a passkey first.",
          statusCode: StatusCodes.Status400BadRequest);
      }

      var removed = await users.RemoveLoginAsync(user, provider, providerKey);
      if (!removed.Succeeded)
      {
        return ManageEndpoints.ValidationProblem(removed);
      }

      // Removing a login changes the security stamp, unlike adding one; this session keeps a renewed cookie.
      await ManageEndpoints.RefreshCookieAsync(context, signIn, user);
      logger.LogInformation("User {UserId} removed a {Provider} login.", user.Id, provider);
      return Results.NoContent();
    });

    return endpoints;
  }

  /// <summary>The providers the server is configured for, for the sign-in page's buttons.</summary>
  public static async Task<IReadOnlyList<ExternalProvider>> ProvidersAsync(SignInManager<UserRecord> signIn) =>
    [.. (await signIn.GetExternalAuthenticationSchemesAsync()).Select(x => new ExternalProvider
    {
      Scheme = x.Name,
      DisplayName = x.DisplayName ?? x.Name,
    })];

  private static async Task<bool> IsConfiguredAsync(SignInManager<UserRecord> signIn, string provider) =>
    (await signIn.GetExternalAuthenticationSchemesAsync()).Any(x => x.Name == provider);

  // Besides external logins, a password or a passkey also signs the user in.
  private static async Task<bool> HasOtherWayToSignInAsync(UserManager<UserRecord> users, UserRecord user) =>
    await users.HasPasswordAsync(user) || (await users.GetPasskeysAsync(user)).Count > 0;

  // Only addresses on this site: a return address from the query must not send the user elsewhere.
  private static string LocalPath(string? returnUrl) =>
    returnUrl is ['/', not '/' and not '\\', ..] or "/" ? returnUrl : "/";
}
