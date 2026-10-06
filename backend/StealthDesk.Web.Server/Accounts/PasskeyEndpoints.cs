using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using StealthDesk.Contracts.Accounts;

namespace StealthDesk.Web.Server.Accounts;

/// <summary>
/// Passkeys (WebAuthn). Each operation is two calls: the server hands out options with a one-time challenge, the
/// browser has the authenticator answer it, and the server checks the answer against the challenge it kept.
/// </summary>
public static class PasskeyEndpoints
{
  private const int MaxNameLength = 200;

  public static IEndpointRouteBuilder MapPasskeyEndpoints(this IEndpointRouteBuilder endpoints)
  {
    var manage = endpoints.MapGroup(Routes.Passkeys).RequireAuthorization();

    manage.MapGet("/", async (ClaimsPrincipal principal, UserManager<UserRecord> users) =>
    {
      if (await users.GetUserAsync(principal) is not { } user)
      {
        return Results.Unauthorized();
      }

      var passkeys = await users.GetPasskeysAsync(user);
      return Results.Ok(passkeys.OrderBy(x => x.CreatedAt).Select(Summary).ToList());
    });

    manage.MapPost("/creation-options", async (ClaimsPrincipal principal, SignInManager<UserRecord> signIn) =>
    {
      if (await signIn.UserManager.GetUserAsync(principal) is not { } user)
      {
        return Results.Unauthorized();
      }

      var name = user.Email ?? user.UserName ?? "User";
      var options = await signIn.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
      {
        Id = user.Id.ToString(),
        Name = name,
        DisplayName = name,
      });
      return Results.Content(options, "application/json");
    });

    manage.MapPost("/", async (PasskeyCredential credential, ClaimsPrincipal principal, SignInManager<UserRecord> signIn, ILogger<UserRecord> logger) =>
    {
      var users = signIn.UserManager;
      if (await users.GetUserAsync(principal) is not { } user)
      {
        return Results.Unauthorized();
      }

      var attestation = await signIn.PerformPasskeyAttestationAsync(credential.CredentialJson);
      if (!attestation.Succeeded)
      {
        logger.LogWarning("User {UserId} could not add a passkey: {Reason}", user.Id, attestation.Failure.Message);
        return Results.Problem($"The passkey couldn't be added: {attestation.Failure.Message}", statusCode: StatusCodes.Status400BadRequest);
      }

      var added = await users.AddOrUpdatePasskeyAsync(user, attestation.Passkey);
      if (!added.Succeeded)
      {
        return ManageEndpoints.ValidationProblem(added);
      }

      logger.LogInformation("User {UserId} added a passkey.", user.Id);
      return Results.Ok(Summary(attestation.Passkey));
    });

    manage.MapPut("/{id}", async (string id, PasskeyRename rename, ClaimsPrincipal principal, UserManager<UserRecord> users) =>
    {
      if (await users.GetUserAsync(principal) is not { } user)
      {
        return Results.Unauthorized();
      }

      var name = rename.Name.Trim();
      if (name.Length is 0 or > MaxNameLength)
      {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
          ["InvalidPasskeyName"] = [$"Give the passkey a name of up to {MaxNameLength} characters."],
        });
      }

      if (CredentialId(id) is not { } credentialId || await users.GetPasskeyAsync(user, credentialId) is not { } passkey)
      {
        return Results.NotFound();
      }

      passkey.Name = name;
      var result = await users.AddOrUpdatePasskeyAsync(user, passkey);
      if (!result.Succeeded)
      {
        return ManageEndpoints.ValidationProblem(result);
      }

      return Results.Ok(Summary(passkey));
    });

    manage.MapDelete("/{id}", async (string id, ClaimsPrincipal principal, UserManager<UserRecord> users, ILogger<UserRecord> logger) =>
    {
      if (await users.GetUserAsync(principal) is not { } user)
      {
        return Results.Unauthorized();
      }

      if (CredentialId(id) is not { } credentialId || await users.GetPasskeyAsync(user, credentialId) is null)
      {
        return Results.NotFound();
      }

      var result = await users.RemovePasskeyAsync(user, credentialId);
      if (!result.Succeeded)
      {
        return ManageEndpoints.ValidationProblem(result);
      }

      logger.LogInformation("User {UserId} removed a passkey.", user.Id);
      return Results.NoContent();
    });

    var signInGroup = endpoints.MapGroup(Routes.SignInPasskey);

    // With an email, the options name that user's passkeys; without one, the browser offers any passkey it has here.
    signInGroup.MapPost("/request-options", async (string? email, SignInManager<UserRecord> signIn) =>
    {
      var user = string.IsNullOrWhiteSpace(email) ? null : await signIn.UserManager.FindByEmailAsync(email.Trim());
      return Results.Content(await signIn.MakePasskeyRequestOptionsAsync(user), "application/json");
    });

    signInGroup.MapPost("/", async (PasskeyCredential credential, SignInManager<UserRecord> signIn) =>
    {
      var result = await signIn.PasskeySignInAsync(credential.CredentialJson);
      return result.Succeeded ? Results.Ok() : Results.Problem(result.ToString(), statusCode: StatusCodes.Status401Unauthorized);
    });

    return endpoints;
  }

  private static PasskeySummary Summary(UserPasskeyInfo passkey) => new()
  {
    Id = WebEncoders.Base64UrlEncode(passkey.CredentialId),
    Name = passkey.Name,
    CreatedAt = passkey.CreatedAt,
    IsBackedUp = passkey.IsBackedUp,
  };

  private static byte[]? CredentialId(string id)
  {
    try
    {
      return WebEncoders.Base64UrlDecode(id);
    }
    catch (FormatException)
    {
      return null;
    }
  }
}
