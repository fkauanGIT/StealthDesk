using Microsoft.AspNetCore.Identity;
using StealthDesk.Web.Server.Accounts;
using StealthDesk.Web.Server.Permissions;
using StealthDesk.Web.Server.PersonalAccessTokens;
using StealthDesk.Web.Server.ServiceAccounts;
using StealthDesk.Web.Server.Users;

namespace StealthDesk.Web.Server.Bootstrap;

/// <summary>
/// Creates what <see cref="BootstrapOptions"/> describes. The administrator and their token are made only while the
/// server has no users, so a later start never creates or changes them, even with other settings; the service account
/// only while none by its name exists.
/// </summary>
public sealed class ServerBootstrap(
  StealthDeskDb db,
  UserManager<UserRecord> users,
  PermissionSeeder seeder,
  PersonalAccessTokenService tokens,
  ServiceAccountService serviceAccounts,
  IOptions<BootstrapOptions> options,
  ILogger<ServerBootstrap> logger)
{
  public async Task RunAsync(CancellationToken cancellationToken)
  {
    var settings = options.Value;
    await CreateAdministratorAsync(settings, cancellationToken);

    if (await serviceAccounts.BootstrapAsync(settings, cancellationToken))
    {
      // The credential's id, never its secret.
      logger.LogInformation(
        "Created the bootstrap server service account {Name} with credential {CredentialId}.",
        settings.ServerServiceAccountName,
        settings.ServerServiceAccountCredentialId);
    }
  }

  private async Task CreateAdministratorAsync(BootstrapOptions settings, CancellationToken cancellationToken)
  {
    if (!settings.HasAdmin)
    {
      return;
    }

    if (await db.Users.IgnoreQueryFilters().AnyAsync(cancellationToken))
    {
      logger.LogInformation("The bootstrap administrator is skipped: the server already has users.");
      return;
    }

    // Like the first registration: a tenant of their own, and the server's administration with it.
    var email = settings.AdminEmail!.Trim();
    var user = new UserRecord { UserName = email, Email = email, Tenant = new TenantRecord() };
    var created = await TenantMembers.CreateAsync(users, seeder, user, settings.AdminPassword, Registration.FirstUserPresets, cancellationToken);
    if (!created.Succeeded)
    {
      throw new InvalidOperationException(
        $"The bootstrap administrator can't be created from {BootstrapOptions.Section}:{nameof(BootstrapOptions.AdminEmail)} and " +
        $"{BootstrapOptions.Section}:{nameof(BootstrapOptions.AdminPassword)}: {string.Join(" ", created.Errors.Select(x => x.Description))}");
    }

    logger.LogInformation("Created the bootstrap administrator {Email}.", email);

    if (settings.HasAdminToken)
    {
      await tokens.BootstrapAsync(user, settings.AdminTokenId!.Value, settings.AdminTokenSecret!, cancellationToken);

      // The token's id, never its secret.
      logger.LogInformation("Created the bootstrap administrator's personal access token {TokenId}.", settings.AdminTokenId);
    }
  }
}
