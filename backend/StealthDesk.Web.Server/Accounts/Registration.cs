using Microsoft.AspNetCore.Identity;

namespace StealthDesk.Web.Server.Accounts;

/// <summary>Who may register, and creating the account and tenant when they do.</summary>
public interface IRegistration
{
  /// <summary>Open while the server has no users (unless disabled) or when public registration is on.</summary>
  Task<bool> IsOpenAsync(CancellationToken cancellationToken = default);

  Task<RegistrationResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default);
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
  ILogger<Registration> logger) : IRegistration
{
  public async Task<bool> IsOpenAsync(CancellationToken cancellationToken = default)
  {
    var rules = options.CurrentValue;
    return rules.EnablePublicRegistration
      || (!rules.DisableFirstUserSelfRegistration && !await db.Users.AnyAsync(cancellationToken));
  }

  public async Task<RegistrationResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default)
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
    var created = await users.CreateAsync(user, password);
    if (!created.Succeeded)
    {
      return new RegistrationResult(created, null);
    }

    var firstUser = await db.Users.CountAsync(cancellationToken) == 1;
    var serverAdministrator = firstUser && !options.CurrentValue.DisableFirstUserSelfRegistration;

    await users.AddClaimAsync(user, StealthDeskClaims.Marker(StealthDeskClaims.TenantAdministrator));
    if (serverAdministrator)
    {
      await users.AddClaimAsync(user, StealthDeskClaims.Marker(StealthDeskClaims.ServerAdministrator));

      // Nobody could confirm it otherwise on a server that has no email set up yet.
      await users.ConfirmEmailAsync(user, await users.GenerateEmailConfirmationTokenAsync(user));
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
