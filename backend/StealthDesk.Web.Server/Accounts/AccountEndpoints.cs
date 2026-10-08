using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.Accounts;

public static class AccountEndpoints
{
  public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
  {
    var auth = endpoints.MapGroup(Routes.Auth);

    auth.MapIdentityApi<UserRecord>().AddEndpointFilter(ApplyServerRules);

    auth.MapGet("/settings", async (IRegistration registration, SignInManager<UserRecord> signIn, CancellationToken cancellationToken) =>
      new AccountSettings
      {
        RegistrationOpen = await registration.IsOpenAsync(cancellationToken),
        ExternalProviders = await ExternalLoginEndpoints.ProvidersAsync(signIn),
      });

    auth.MapGet("/me", async (HttpContext context, UserManager<UserRecord> users, StealthDeskDb db, IPermissionEvaluator permissions) =>
    {
      var user = await users.GetUserAsync(context.User);
      if (user is null)
      {
        return Results.Unauthorized();
      }

      var tenantName = await db.Tenants.Where(x => x.Id == user.TenantId).Select(x => x.Name).FirstOrDefaultAsync();
      // Administrators are whoever may manage access, at the server or in their tenant.
      var principal = new Principal(PermissionPrincipalKind.User, user.Id, user.TenantId);
      var server = await permissions.EvaluateAsync(principal, PermissionNames.ServerPermissionsWrite, Resource.Server, context.RequestAborted);
      var tenant = await permissions.EvaluateAsync(principal, PermissionNames.TenantPermissionsWrite, Resource.Tenant(user.TenantId), context.RequestAborted);
      return Results.Ok(new CurrentUser
      {
        Id = user.Id,
        Email = user.Email ?? string.Empty,
        TenantId = user.TenantId,
        TenantName = tenantName ?? string.Empty,
        EmailConfirmed = user.EmailConfirmed,
        TwoFactorEnabled = user.TwoFactorEnabled,
        MustChangePassword = user.MustChangePassword,
        IsServerAdministrator = server.Allowed,
        IsTenantAdministrator = tenant.Allowed,
      });
    }).RequireAuthorization();

    // Identity's endpoints have no sign-out: the cookie is removed here.
    auth.MapPost("/sign-out", async (SignInManager<UserRecord> signIn) =>
    {
      await signIn.SignOutAsync();
      return Results.NoContent();
    }).RequireAuthorization();

    return endpoints;
  }

  // Identity's endpoints don't know StealthDesk's rules: who may register, and whether bearer tokens are allowed.
  private static async ValueTask<object?> ApplyServerRules(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
  {
    var request = context.HttpContext.Request;
    var path = request.Path.Value ?? string.Empty;

    if (path.EndsWith("/register", StringComparison.OrdinalIgnoreCase))
    {
      return await RegisterAsync(context);
    }

    // People open this from an email: answer with the web client's page instead of Identity's plain text.
    if (path.EndsWith("/confirmEmail", StringComparison.OrdinalIgnoreCase))
    {
      if (!Succeeded(await next(context)))
      {
        return Results.Redirect("/account/email-confirmed?failed=true");
      }

      if (!request.Query.ContainsKey("changedEmail"))
      {
        return Results.Redirect("/account/email-confirmed");
      }

      // The change renewed the security stamp: when the link is opened where the user is signed in, keep them signed in.
      var signIn = context.HttpContext.RequestServices.GetRequiredService<SignInManager<UserRecord>>();
      var userId = request.Query["userId"].ToString();
      if (signIn.UserManager.GetUserId(context.HttpContext.User) == userId
        && await signIn.UserManager.FindByIdAsync(userId) is { } changed)
      {
        await signIn.RefreshSignInAsync(changed);
      }

      return Results.Redirect("/account/email-confirmed?changed=true");
    }

    // A password picked through a reset link ends a forced change, like changing it while signed in.
    if (path.EndsWith("/resetPassword", StringComparison.OrdinalIgnoreCase))
    {
      var outcome = await next(context);
      if (Succeeded(outcome) && context.Arguments.OfType<ResetPasswordRequest>().FirstOrDefault() is { } reset)
      {
        var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<UserRecord>>();
        if (await users.FindByEmailAsync(reset.Email) is { } user)
        {
          await ManageEndpoints.PasswordChosenAsync(users, user);
        }
      }

      return outcome;
    }

    var accounts = context.HttpContext.RequestServices.GetRequiredService<IOptions<AccountOptions>>().Value;
    var wantsCookie = IsTrue(request.Query["useCookies"]) || IsTrue(request.Query["useSessionCookies"]);
    var asksForTokens = (path.EndsWith("/login", StringComparison.OrdinalIgnoreCase) && !wantsCookie)
      || path.EndsWith("/refresh", StringComparison.OrdinalIgnoreCase);

    if (asksForTokens && !accounts.EnableBearerLogin)
    {
      return Results.Problem("Bearer sign-in is disabled on this server.", statusCode: StatusCodes.Status400BadRequest);
    }

    return await next(context);
  }

  // Replaces Identity's register, which would create a user without a tenant.
  private static async Task<IResult> RegisterAsync(EndpointFilterInvocationContext context)
  {
    if (context.Arguments.OfType<RegisterRequest>().FirstOrDefault() is not { } request)
    {
      return Results.Problem("Invalid registration request.", statusCode: StatusCodes.Status400BadRequest);
    }

    var registration = context.HttpContext.RequestServices.GetRequiredService<IRegistration>();
    var result = await registration.RegisterAsync(request.Email, request.Password, context.HttpContext.RequestAborted);

    if (result.WasClosed)
    {
      // Closed looks like missing: nothing to learn about the server from trying.
      return Results.NotFound();
    }

    if (!result.Succeeded)
    {
      return ManageEndpoints.ValidationProblem(result.Identity);
    }

    return Results.Ok();
  }

  private static bool IsTrue(string? value) => bool.TryParse(value, out var result) && result;

  private static bool Succeeded(object? outcome) =>
    Unwrap(outcome) is not (IStatusCodeHttpResult { StatusCode: >= 400 } or UnauthorizedHttpResult);

  // Identity's endpoints answer with Results<A, B>, which wraps the result that actually ran.
  private static object? Unwrap(object? result) => result is INestedHttpResult nested ? Unwrap(nested.Result) : result;
}
