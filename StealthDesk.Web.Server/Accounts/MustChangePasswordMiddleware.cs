using Microsoft.AspNetCore.Identity;

namespace StealthDesk.Web.Server.Accounts;

/// <summary>
/// Keeps a user whose password must change away from the API and hubs until they change it. Pages still load: the
/// web client sees the flag on /me and shows the password change.
/// </summary>
public sealed class MustChangePasswordMiddleware(RequestDelegate next)
{
  // Enough to know who is signed in, sign out, and change the password.
  private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
  {
    Routes.CurrentUser,
    Routes.SignOut,
    Routes.AuthSettings,
    Routes.AccountPassword,
  };

  public async Task InvokeAsync(HttpContext context, UserManager<UserRecord> users)
  {
    var path = context.Request.Path;
    var guarded = path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs");

    if (!guarded
      || context.User.Identity?.IsAuthenticated != true
      || Allowed.Contains(path.Value ?? string.Empty)
      || await users.GetUserAsync(context.User) is not { MustChangePassword: true })
    {
      await next(context);
      return;
    }

    await Results.Problem("Password change required.", statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
  }
}
