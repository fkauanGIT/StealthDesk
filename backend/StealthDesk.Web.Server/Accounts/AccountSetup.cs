using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Branding;

namespace StealthDesk.Web.Server.Accounts;

public static class AccountSetup
{
  /// <summary>Picks the cookie or, when enabled and sent, the bearer token.</summary>
  public const string Scheme = "StealthDesk";

  /// <summary>
  /// Users through ASP.NET Core Identity: browsers sign in with a cookie, scripts with bearer tokens when enabled.
  /// Data protection keys are kept in the database.
  /// </summary>
  public static WebApplicationBuilder AddStealthDeskAccounts(this WebApplicationBuilder builder)
  {
    var section = builder.Configuration.GetSection(AccountOptions.Section);
    var accounts = section.Get<AccountOptions>() ?? new AccountOptions();
    builder.Services.Configure<AccountOptions>(section);

    builder.Services
      .AddIdentityApiEndpoints<UserRecord>(options =>
      {
        ConfigureStores(options);
        options.User.RequireUniqueEmail = accounts.RequireUniqueEmail;
        options.SignIn.RequireConfirmedEmail = accounts.RequireConfirmedEmail;
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.AllowedForNewUsers = true;
      })
      .AddEntityFrameworkStores<StealthDeskDb>()
      .AddSignInManager<StealthDeskSignInManager>()
      .AddClaimsPrincipalFactory<StealthDeskClaimsFactory>()
      .AddDefaultTokenProviders();

    var authentication = builder.Services
      .AddAuthentication(options =>
      {
        options.DefaultScheme = Scheme;

        // Microsoft and GitHub hand the user back to Identity's short-lived external cookie, read by the callback.
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
      })
      // No display name: Identity lists every scheme that has one as an external sign-in provider.
      .AddPolicyScheme(Scheme, displayName: null, options =>
      {
        options.ForwardDefaultSelector = context =>
          accounts.EnableBearerLogin
          && context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? IdentityConstants.BearerScheme
            : IdentityConstants.ApplicationScheme;
      });

    // A provider exists only when it is configured, so the sign-in page shows only the ones that work.
    if (!string.IsNullOrWhiteSpace(accounts.MicrosoftClientId) && !string.IsNullOrWhiteSpace(accounts.MicrosoftClientSecret))
    {
      authentication.AddMicrosoftAccount(options =>
      {
        options.ClientId = accounts.MicrosoftClientId;
        options.ClientSecret = accounts.MicrosoftClientSecret;
        BackToSignIn(options);
      });
    }

    if (!string.IsNullOrWhiteSpace(accounts.GitHubClientId) && !string.IsNullOrWhiteSpace(accounts.GitHubClientSecret))
    {
      authentication.AddGitHub(options =>
      {
        options.ClientId = accounts.GitHubClientId;
        options.ClientSecret = accounts.GitHubClientSecret;

        // Without it GitHub gives the email only when the user made it public.
        options.Scope.Add("user:email");
        BackToSignIn(options);
      });
    }

    builder.Services.Configure<BearerTokenOptions>(IdentityConstants.BearerScheme, options =>
    {
      options.BearerTokenExpiration = accounts.BearerTokenLifetime;
      options.RefreshTokenExpiration = accounts.RefreshTokenLifetime;
    });

    builder.Services.ConfigureApplicationCookie(options =>
    {
      options.LoginPath = "/account/sign-in";
      options.AccessDeniedPath = "/account/access-denied";

      // The API and hubs answer with a status code; only pages are sent to the sign-in page.
      options.Events.OnRedirectToLogin = context => Answer(context, StatusCodes.Status401Unauthorized);
      options.Events.OnRedirectToAccessDenied = context => Answer(context, StatusCodes.Status403Forbidden);
    });

    builder.Services.AddAuthorization();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ITenantScope, RequestTenantScope>();
    builder.Services.AddSingleton<RegistrationGate>();
    builder.Services.AddScoped<IRegistration, Registration>();

    // Keys in the database instead of the machine: cookies stay valid after a restart and across servers.
    builder.Services
      .AddDataProtection()
      .SetApplicationName(Brand.Name)
      .PersistKeysToDbContext<StealthDeskDb>();

    return builder;
  }

  /// <summary>
  /// The Identity schema with passkeys. Shared with the design-time database factory: the model reads it from
  /// these options, so migrations must see the same value as the running server.
  /// </summary>
  public static void ConfigureStores(IdentityOptions options) => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;

  /// <summary>Turning the provider down, or the provider failing, ends on the sign-in page instead of an error page.</summary>
  public static void BackToSignIn(RemoteAuthenticationOptions options)
  {
    options.Events.OnAccessDenied = context =>
    {
      context.Response.Redirect("/account/sign-in?external=cancelled");
      context.HandleResponse();
      return Task.CompletedTask;
    };
    options.Events.OnRemoteFailure = context =>
    {
      context.Response.Redirect("/account/sign-in?external=failed");
      context.HandleResponse();
      return Task.CompletedTask;
    };
  }

  private static Task Answer(RedirectContext<CookieAuthenticationOptions> context, int status)
  {
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
    {
      context.Response.StatusCode = status;
    }
    else
    {
      context.Response.Redirect(context.RedirectUri);
    }

    return Task.CompletedTask;
  }
}
