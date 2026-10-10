using Microsoft.AspNetCore.Identity;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.Users;

/// <summary>Accounts added to an existing tenant by someone already in it, through the API or an invite.</summary>
public static class TenantMembers
{
  /// <summary>
  /// Creates the account confirmed, since whoever adds it vouches for the address, holding the baseline and the
  /// presets given. Without a password it can't sign in with one until a password is set.
  /// </summary>
  public static async Task<IdentityResult> CreateAsync(
    UserManager<UserRecord> users,
    PermissionSeeder seeder,
    UserRecord user,
    string? password,
    IEnumerable<string> presets,
    CancellationToken cancellationToken)
  {
    var created = string.IsNullOrEmpty(password) ? await users.CreateAsync(user) : await users.CreateAsync(user, password);
    if (!created.Succeeded)
    {
      return created;
    }

    await users.ConfirmEmailAsync(user, await users.GenerateEmailConfirmationTokenAsync(user));
    await seeder.SeedAsync(user.Id, user.TenantId, PermissionPresets.Baseline.Concat(presets), cancellationToken);
    return created;
  }
}
