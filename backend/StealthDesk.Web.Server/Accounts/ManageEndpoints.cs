using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Contracts.Accounts;

namespace StealthDesk.Web.Server.Accounts;

/// <summary>
/// The signed-in user managing their own account. Changing the email is Identity's /manage/info, which confirms the
/// new address before applying it.
/// </summary>
public static class ManageEndpoints
{
  public static IEndpointRouteBuilder MapManageEndpoints(this IEndpointRouteBuilder endpoints)
  {
    var account = endpoints.MapGroup(Routes.Account).RequireAuthorization();

    account.MapGet("/profile", async (ClaimsPrincipal principal, UserManager<UserRecord> users) =>
      await users.GetUserAsync(principal) is { } user ? Results.Ok(await ProfileAsync(users, user)) : Results.Unauthorized());

    account.MapPut("/profile", async (ProfileUpdate update, HttpContext context, SignInManager<UserRecord> signIn) =>
    {
      var users = signIn.UserManager;
      if (await users.GetUserAsync(context.User) is not { } user)
      {
        return Results.Unauthorized();
      }

      var phone = string.IsNullOrWhiteSpace(update.PhoneNumber) ? null : update.PhoneNumber.Trim();
      if (phone is not null && !new PhoneAttribute().IsValid(phone))
      {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["InvalidPhoneNumber"] = ["This isn't a valid phone number."] });
      }

      if (phone != user.PhoneNumber)
      {
        var result = await users.SetPhoneNumberAsync(user, phone);
        if (!result.Succeeded)
        {
          return ValidationProblem(result);
        }

        // A new phone number changes the security stamp, which would end this session too.
        await RefreshCookieAsync(context, signIn, user);
      }

      return Results.Ok(await ProfileAsync(users, user));
    });

    account.MapPost("/password", async (PasswordChange change, HttpContext context, SignInManager<UserRecord> signIn) =>
    {
      var users = signIn.UserManager;
      if (await users.GetUserAsync(context.User) is not { } user)
      {
        return Results.Unauthorized();
      }

      var result = await users.ChangePasswordAsync(user, change.CurrentPassword, change.NewPassword);
      if (!result.Succeeded)
      {
        return ValidationProblem(result);
      }

      await PasswordChosenAsync(users, user);

      // The new security stamp signs out every other session; this one gets a cookie with the new stamp.
      await RefreshCookieAsync(context, signIn, user);
      return Results.NoContent();
    });

    account.MapPost("/password/set", async (PasswordSet set, HttpContext context, SignInManager<UserRecord> signIn) =>
    {
      var users = signIn.UserManager;
      if (await users.GetUserAsync(context.User) is not { } user)
      {
        return Results.Unauthorized();
      }

      if (await users.HasPasswordAsync(user))
      {
        return Results.Problem("This account already has a password. Change it instead.", statusCode: StatusCodes.Status400BadRequest);
      }

      var result = await users.AddPasswordAsync(user, set.NewPassword);
      if (!result.Succeeded)
      {
        return ValidationProblem(result);
      }

      await RefreshCookieAsync(context, signIn, user);
      return Results.NoContent();
    });

    account.MapGet("/personal-data", async (ClaimsPrincipal principal, UserManager<UserRecord> users, ILogger<UserRecord> logger) =>
    {
      if (await users.GetUserAsync(principal) is not { } user)
      {
        return Results.Unauthorized();
      }

      logger.LogInformation("User {UserId} downloaded their personal data.", user.Id);
      return Results.File(await PersonalDataAsync(users, user), "application/json", "PersonalData.json");
    });

    account.MapPost("/delete", async (AccountDeletion deletion, HttpContext context, SignInManager<UserRecord> signIn, ILogger<UserRecord> logger) =>
    {
      var users = signIn.UserManager;
      if (await users.GetUserAsync(context.User) is not { } user)
      {
        return Results.Unauthorized();
      }

      if (await users.HasPasswordAsync(user) && !await users.CheckPasswordAsync(user, deletion.Password ?? string.Empty))
      {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["PasswordMismatch"] = ["Incorrect password."] });
      }

      var result = await users.DeleteAsync(user);
      if (!result.Succeeded)
      {
        return ValidationProblem(result);
      }

      await signIn.SignOutAsync();
      logger.LogInformation("User {UserId} deleted their account.", user.Id);
      return Results.NoContent();
    });

    return endpoints;
  }

  /// <summary>Clears the forced password change once the user has picked a password of their own.</summary>
  public static async Task PasswordChosenAsync(UserManager<UserRecord> users, UserRecord user)
  {
    if (user.MustChangePassword)
    {
      user.MustChangePassword = false;
      await users.UpdateAsync(user);
    }
  }

  private static async Task<AccountProfile> ProfileAsync(UserManager<UserRecord> users, UserRecord user) => new()
  {
    Id = user.Id,
    Email = user.Email ?? string.Empty,
    EmailConfirmed = user.EmailConfirmed,
    PhoneNumber = user.PhoneNumber,
    HasPassword = await users.HasPasswordAsync(user),
  };

  // Browsers keep the session in a cookie that must carry the new security stamp. Bearer tokens have nothing to renew.
  private static async Task RefreshCookieAsync(HttpContext context, SignInManager<UserRecord> signIn, UserRecord user)
  {
    if (context.User.Identity?.AuthenticationType == IdentityConstants.ApplicationScheme)
    {
      await signIn.RefreshSignInAsync(user);
    }
  }

  // What Identity marks as personal data, plus the user's logins and authenticator key.
  private static async Task<byte[]> PersonalDataAsync(UserManager<UserRecord> users, UserRecord user)
  {
    var data = typeof(UserRecord).GetProperties()
      .Where(x => x.IsDefined(typeof(PersonalDataAttribute), inherit: true))
      .ToDictionary(x => x.Name, x => x.GetValue(user));

    foreach (var login in await users.GetLoginsAsync(user))
    {
      data[$"{login.LoginProvider} external login provider key"] = login.ProviderKey;
    }

    data["Authenticator Key"] = await users.GetAuthenticatorKeyAsync(user);

    // Values keep their JSON types: ISO dates and true/false, the same in any server culture. The file is
    // downloaded, never placed in a page, so characters like "+" don't need escaping.
    return JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions
    {
      WriteIndented = true,
      Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
  }

  /// <summary>Identity's errors keyed by their code, so the pages can show each one next to its field.</summary>
  public static IResult ValidationProblem(IdentityResult result) =>
    Results.ValidationProblem(result.Errors
      .GroupBy(x => string.IsNullOrWhiteSpace(x.Code) ? nameof(IdentityError) : x.Code)
      .ToDictionary(x => x.Key, x => x.Select(error => error.Description).ToArray()));
}
