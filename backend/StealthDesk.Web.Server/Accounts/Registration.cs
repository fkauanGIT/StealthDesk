using Microsoft.AspNetCore.Identity;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.Permissions;
using StealthDesk.Web.Server.Email;

namespace StealthDesk.Web.Server.Accounts;

/// <summary>Who may register, and creating the account and tenant when they do.</summary>
public interface IRegistration
{
  /// <summary>Open while the server has no users (unless disabled) or when public registration is on.</summary>
  Task<bool> IsOpenAsync(CancellationToken cancellationToken = default);

  Task<RegistrationResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default);

  /// <summary>A first sign-in with Microsoft or GitHub: an account without a password, linked to that login.</summary>
  Task<RegistrationResult> RegisterExternalAsync(string email, UserLoginInfo login, CancellationToken cancellationToken = default);
}

public sealed record RegistrationResult(IdentityResult Identity, UserRecord? User)
{
  public const string ClosedCode = "RegistrationClosed";

  public bool Succeeded => Identity.Succeeded;

  public bool WasClosed => Identity.Errors.Any(x => x.Code == ClosedCode);
}

public sealed class Registration(
  UserManager<UserRecord> users,
  StealthDeskDb db,
  RegistrationGate gate,
  IOptionsMonitor<AccountOptions> options,
  IOptionsMonitor<EmailOptions> emailOptions,
  AccountEmails emails,
  PermissionSeeder permissions,
  ILogger<Registration> logger) : IRegistration
{
  public async Task<bool> IsOpenAsync(CancellationToken cancellationToken = default)
  {
    var rules = options.CurrentValue;
    return rules.EnablePublicRegistration
      || (!rules.DisableFirstUserSelfRegistration && !await db.Users.AnyAsync(cancellationToken));
  }

  public Task<RegistrationResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default) =>
    CreateAsync(email, user => users.CreateAsync(user, password), cancellationToken);

  public async Task<RegistrationResult> RegisterExternalAsync(string email, UserLoginInfo login, CancellationToken cancellationToken = default)
  {
    if (await users.FindByLoginAsync(login.LoginProvider, login.ProviderKey) is not null)
    {
      return new RegistrationResult(IdentityResult.Failed(new IdentityErrorDescriber().LoginAlreadyAssociated()), null);
    }

    return await CreateAsync(email, async user =>
    {
      var created = await users.CreateAsync(user);
      if (!created.Succeeded)
      {
        return created;
      }

      // An account without its login would have no way to sign in.
      var linked = await users.AddLoginAsync(user, login);
      if (!linked.Succeeded)
      {
        await users.DeleteAsync(user);
        db.Tenants.Remove(user.Tenant!);
        await db.SaveChangesAsync(cancellationToken);
      }

      return linked;
    }, cancellationToken);
  }

  private async Task<RegistrationResult> CreateAsync(string email, Func<UserRecord, Task<IdentityResult>> create, CancellationToken cancellationToken)
  {
    // One at a time: two registrations on an empty server must not both become its administrator.
    using var turn = await gate.WaitAsync(cancellationToken);

    if (!await IsOpenAsync(cancellationToken))
    {
      logger.LogWarning("Registration refused for {Email}: registration is closed.", email);
      return new RegistrationResult(
        IdentityResult.Failed(new IdentityError { Code = RegistrationResult.ClosedCode, Description = "Registration is closed." }),
        null);
    }

    // Without an invite, every account starts its own tenant.
    var user = new UserRecord { UserName = email, Email = email, Tenant = new TenantRecord() };
    var created = await create(user);
    if (!created.Succeeded)
    {
      return new RegistrationResult(created, null);
    }

    var firstUser = await db.Users.CountAsync(cancellationToken) == 1;
    var serverAdministrator = firstUser && !options.CurrentValue.DisableFirstUserSelfRegistration;

    // The account created its tenant, so it administers it; the server's first one administers the server too.
    var presets = PermissionPresets.TenantCreator.Concat(PermissionPresets.Baseline);
    await permissions.SeedAsync(user.Id, user.TenantId, serverAdministrator ? PermissionPresets.FirstUser.Concat(presets) : presets, cancellationToken);

    // The first user, and anyone on a server that sends no email, could never get the link: confirmed right away.
    var confirmation = await users.GenerateEmailConfirmationTokenAsync(user);
    if (serverAdministrator || emailOptions.CurrentValue.DisableSending)
    {
      await users.ConfirmEmailAsync(user, confirmation);
    }
    else
    {
      await emails.SendRegistrationConfirmationAsync(user, confirmation);
    }

    logger.LogInformation(
      "Registered {Email} in a new tenant{Role}.",
      email,
      serverAdministrator ? " as the server administrator" : string.Empty);

    return new RegistrationResult(created, user);
  }
}

/// <summary>Lets one registration run at a time in this server.</summary>
public sealed class RegistrationGate : IDisposable
{
  private readonly SemaphoreSlim _semaphore = new(1, 1);

  public async Task<IDisposable> WaitAsync(CancellationToken cancellationToken)
  {
    await _semaphore.WaitAsync(cancellationToken);
    return new Turn(_semaphore);
  }

  public void Dispose() => _semaphore.Dispose();

  private sealed class Turn(SemaphoreSlim semaphore) : IDisposable
  {
    private int _released;

    public void Dispose()
    {
      if (Interlocked.Exchange(ref _released, 1) == 0)
      {
        semaphore.Release();
      }
    }
  }
}
