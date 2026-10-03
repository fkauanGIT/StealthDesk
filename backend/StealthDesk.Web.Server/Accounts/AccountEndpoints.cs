using Microsoft.AspNetCore.Identity;
using StealthDesk.Contracts.Accounts;

namespace StealthDesk.Web.Server.Accounts;

public static class AccountEndpoints
{
  public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
  {
    var auth = endpoints.MapGroup(Routes.Auth);

    auth.MapIdentityApi<UserRecord>().AddEndpointFilter(RefuseWhatIsNotEnabled);

    auth.MapGet("/me", async (HttpContext context, UserManager<UserRecord> users, StealthDeskDb db) =>
    {
      var user = await users.GetUserAsync(context.User);
      if (user is null)
      {
        return Results.Unauthorized();
      }

      var tenantName = await db.Tenants.Where(x => x.Id == user.TenantId).Select(x => x.Name).FirstOrDefaultAsync();
      return Results.Ok(new CurrentUser
      {
        Id = user.Id,
        Email = user.Email ?? string.Empty,
        TenantId = user.TenantId,
        TenantName = tenantName ?? string.Empty,
        EmailConfirmed = user.EmailConfirmed,
        TwoFactorEnabled = user.TwoFactorEnabled,
        MustChangePassword = user.MustChangePassword,
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

  // Bearer tokens only when the server allows them; registration rules arrive with the first-user work (#77).
  private static async ValueTask<object?> RefuseWhatIsNotEnabled(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
  {
    var request = context.HttpContext.Request;
    var path = request.Path.Value ?? string.Empty;
    var accounts = context.HttpContext.RequestServices.GetRequiredService<IOptions<AccountOptions>>().Value;

    if (path.EndsWith("/register", StringComparison.OrdinalIgnoreCase))
    {
      return Results.Problem("Registration is not open.", statusCode: StatusCodes.Status403Forbidden);
    }

    var wantsCookie = IsTrue(request.Query["useCookies"]) || IsTrue(request.Query["useSessionCookies"]);
    var asksForTokens = (path.EndsWith("/login", StringComparison.OrdinalIgnoreCase) && !wantsCookie)
      || path.EndsWith("/refresh", StringComparison.OrdinalIgnoreCase);

    if (asksForTokens && !accounts.EnableBearerLogin)
    {
      return Results.Problem("Bearer sign-in is disabled on this server.", statusCode: StatusCodes.Status400BadRequest);
    }

    return await next(context);
  }

  private static bool IsTrue(string? value) => bool.TryParse(value, out var result) && result;
}
