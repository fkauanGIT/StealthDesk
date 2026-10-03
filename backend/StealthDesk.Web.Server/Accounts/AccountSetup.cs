using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Branding;

namespace StealthDesk.Web.Server.Accounts;

public static class AccountSetup
{
  /// <summary>User storage through ASP.NET Core Identity, and the data protection keys kept in the database.</summary>
  public static WebApplicationBuilder AddStealthDeskAccounts(this WebApplicationBuilder builder)
  {
    builder.Services
      .AddIdentityCore<UserRecord>(ConfigureStores)
      .AddEntityFrameworkStores<StealthDeskDb>()
      .AddDefaultTokenProviders();

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
}
