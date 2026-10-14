using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.ServiceAccounts;
using StealthDesk.Web.Server.AuthorizationLogs;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.ServiceAccounts;

/// <summary>A credential that checked out, with the account it signs in as.</summary>
public sealed record ValidServiceAccountCredential(Guid CredentialId, DateTimeOffset? ExpiresAt, Guid AccountId, Guid? TenantId, string AccountName);

/// <summary>
/// Service accounts and their credentials. Every call names whose accounts it works on: a tenant's, or with no
/// tenant the server's. The key a script sends is <c>{credential id}:{secret}</c>; only a hash of the secret is kept.
/// </summary>
public sealed class ServiceAccountService(
  StealthDeskDb db,
  IPermissionEvaluator evaluator,
  IAuthorizationChangeFactory changes,
  IPasswordHasher<ServiceAccountCredentialRecord> hasher,
  IMemoryCache cache,
  TimeProvider clock)
{
  private const string SecretAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  private const int SecretLength = 64;

  // A valid credential is remembered briefly so scripts calling in bursts don't hash on every request. Anything
  // that stops a credential from working forgets it at once.
  private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

  public async Task<IReadOnlyList<ServiceAccount>> ListAsync(Guid? tenant, CancellationToken cancellationToken)
  {
    var accounts = await Accounts(tenant).Include(x => x.Credentials).AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
    return [.. accounts.Select(ToContract)];
  }

  public async Task<IResult> GetAsync(Guid? tenant, Guid id, CancellationToken cancellationToken) =>
    await Accounts(tenant).Include(x => x.Credentials).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is { } account
      ? Results.Ok(ToContract(account))
      : Results.NotFound();

  public async Task<IResult> CreateAsync(Principal actor, Guid? tenant, CreateServiceAccountRequest request, CancellationToken cancellationToken)
  {
    if (DetailsRefusal(request.Name, request.Description) is { } invalid)
    {
      return Invalid(invalid);
    }

    if (!Enum.IsDefined(request.AccessMode) || (tenant is not null && request.AccessMode != ServiceAccountAccessMode.Restricted))
    {
      return Invalid("A tenant service account is always restricted; only a server account can be unrestricted.");
    }

    // An unrestricted account can do anything, so creating one takes the right to manage the server's permissions.
    if (request.AccessMode == ServiceAccountAccessMode.Unrestricted
      && !(await evaluator.EvaluateAsync(actor, PermissionNames.ServerPermissionsWrite, Resource.Server, cancellationToken)).Allowed)
    {
      return Results.Problem($"The '{PermissionNames.ServerPermissionsWrite}' permission is required to create an unrestricted account.", statusCode: StatusCodes.Status403Forbidden);
    }

    var name = request.Name.Trim();
    if (await NameTakenAsync(tenant, name, except: null, cancellationToken))
    {
      return NameConflict(tenant);
    }

    var account = new ServiceAccountRecord
    {
      Id = Guid.NewGuid(),
      Kind = tenant is null ? ServiceAccountKind.Server : ServiceAccountKind.Tenant,
      TenantId = tenant,
      Name = name,
      Description = Clean(request.Description),
      AccessMode = request.AccessMode,
      CreatedAt = clock.GetUtcNow(),
    };
    db.ServiceAccounts.Add(account);
    db.AuthorizationChanges.Add(changes.Create(
      AuthorizationChangeActions.ServiceAccountCreated, actor, AuthorizationChangeTargets.ServiceAccount, account.Id, tenant,
      after: ServiceAccountSnapshot.Of(account)));
    if (await SaveAsync(tenant, cancellationToken) is { } conflict)
    {
      return conflict;
    }

    return Results.Created(Routes.ServiceAccount(Base(tenant), account.Id), ToContract(account));
  }

  public async Task<IResult> UpdateAsync(Principal actor, Guid? tenant, Guid id, UpdateServiceAccountRequest request, CancellationToken cancellationToken)
  {
    if (DetailsRefusal(request.Name, request.Description) is { } invalid)
    {
      return Invalid(invalid);
    }

    if (await Accounts(tenant).Include(x => x.Credentials).FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } account)
    {
      return Results.NotFound();
    }

    var name = request.Name.Trim();
    if (await NameTakenAsync(tenant, name, except: id, cancellationToken))
    {
      return NameConflict(tenant);
    }

    var before = ServiceAccountSnapshot.Of(account);
    account.Name = name;
    account.Description = Clean(request.Description);
    account.IsEnabled = request.IsEnabled;
    db.AuthorizationChanges.Add(changes.Create(
      AuthorizationChangeActions.ServiceAccountUpdated, actor, AuthorizationChangeTargets.ServiceAccount, id, tenant,
      before, ServiceAccountSnapshot.Of(account)));
    if (await SaveAsync(tenant, cancellationToken) is { } conflict)
    {
      return conflict;
    }

    if (before.IsEnabled != account.IsEnabled)
    {
      Forget(account.Credentials);
    }

    return Results.Ok(ToContract(account));
  }

  /// <summary>Removes the account, its credentials and its permissions.</summary>
  public async Task<IResult> DeleteAsync(Principal actor, Guid? tenant, Guid id, CancellationToken cancellationToken)
  {
    if (actor.Kind == PermissionPrincipalKind.ServiceAccount && actor.Id == id)
    {
      return Results.Problem("A service account can't delete itself.", statusCode: StatusCodes.Status403Forbidden);
    }

    if (await Accounts(tenant).Include(x => x.Credentials).FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } account)
    {
      return Results.NotFound();
    }

    db.PermissionAssignments.RemoveRange(await db.PermissionAssignments
      .IgnoreQueryFilters()
      .Where(x => x.PrincipalKind == PermissionPrincipalKind.ServiceAccount && x.PrincipalId == id)
      .ToListAsync(cancellationToken));
    db.AuthorizationChanges.Add(changes.Create(
      AuthorizationChangeActions.ServiceAccountDeleted, actor, AuthorizationChangeTargets.ServiceAccount, id, tenant,
      before: ServiceAccountSnapshot.Of(account)));
    db.ServiceAccounts.Remove(account);
    await db.SaveChangesAsync(cancellationToken);
    Forget(account.Credentials);
    return Results.NoContent();
  }

  /// <summary>Adds a credential; its key is in the answer and never shown again. Rotating is adding one, then revoking the old.</summary>
  public async Task<IResult> AddCredentialAsync(Principal actor, Guid? tenant, Guid id, CreateServiceAccountCredentialRequest request, CancellationToken cancellationToken)
  {
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > CreateServiceAccountCredentialRequest.NameMax)
    {
      return Invalid($"Enter a credential name of at most {CreateServiceAccountCredentialRequest.NameMax} characters.");
    }

    var now = clock.GetUtcNow();
    if (request.ExpiresAt <= now)
    {
      return Invalid("A credential must expire in the future, or never.");
    }

    if (await Accounts(tenant).FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } account)
    {
      return Results.NotFound();
    }

    if (!account.IsEnabled)
    {
      return Results.Problem("The service account is disabled; enable it before adding a credential.", statusCode: StatusCodes.Status403Forbidden);
    }

    var credential = new ServiceAccountCredentialRecord
    {
      Id = Guid.NewGuid(),
      ServiceAccountId = id,
      Name = request.Name.Trim(),
      CreatedAt = now,
      ExpiresAt = request.ExpiresAt,
    };
    var secret = RandomNumberGenerator.GetString(SecretAlphabet, SecretLength);
    credential.HashedSecret = hasher.HashPassword(credential, secret);
    db.ServiceAccountCredentials.Add(credential);
    db.AuthorizationChanges.Add(changes.Create(
      AuthorizationChangeActions.ServiceAccountCredentialCreated, actor, AuthorizationChangeTargets.ServiceAccountCredential, credential.Id, tenant,
      after: ServiceAccountCredentialSnapshot.Of(credential)));
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created(
      Routes.ServiceAccountCredential(Base(tenant), id, credential.Id),
      new CreatedServiceAccountCredential(ToContract(credential), $"{credential.Id}:{secret}"));
  }

  /// <summary>Stops the credential from working, at once. Revoking one already revoked changes nothing.</summary>
  public async Task<IResult> RevokeCredentialAsync(Principal actor, Guid? tenant, Guid id, Guid credentialId, CancellationToken cancellationToken)
  {
    if (await CredentialAsync(tenant, id, credentialId, cancellationToken) is not { } credential)
    {
      return Results.NotFound();
    }

    if (credential.RevokedAt is null)
    {
      credential.RevokedAt = clock.GetUtcNow();
      db.AuthorizationChanges.Add(changes.Create(
        AuthorizationChangeActions.ServiceAccountCredentialRevoked, actor, AuthorizationChangeTargets.ServiceAccountCredential, credentialId, tenant,
        before: ServiceAccountCredentialSnapshot.Of(credential)));
      await db.SaveChangesAsync(cancellationToken);
      Forget([credential]);
    }

    return Results.NoContent();
  }

  /// <summary>Deletes a credential for good; only one that already stopped working, so nothing in use vanishes.</summary>
  public async Task<IResult> PurgeCredentialAsync(Principal actor, Guid? tenant, Guid id, Guid credentialId, CancellationToken cancellationToken)
  {
    if (await CredentialAsync(tenant, id, credentialId, cancellationToken) is not { } credential)
    {
      return Results.NotFound();
    }

    if (!credential.IsDead(clock.GetUtcNow()))
    {
      return Invalid("Only revoked or expired credentials can be deleted. Revoke it first.");
    }

    db.AuthorizationChanges.Add(changes.Create(
      AuthorizationChangeActions.ServiceAccountCredentialDeleted, actor, AuthorizationChangeTargets.ServiceAccountCredential, credentialId, tenant,
      before: ServiceAccountCredentialSnapshot.Of(credential)));
    db.ServiceAccountCredentials.Remove(credential);
    await db.SaveChangesAsync(cancellationToken);
    Forget([credential]);
    return Results.NoContent();
  }

  /// <summary>
  /// The credential an <c>id:secret</c> key stands for, when its secret matches, it isn't revoked or expired, and
  /// its account is enabled; its last use is recorded.
  /// </summary>
  public async Task<ValidServiceAccountCredential?> ValidateAsync(string presented, CancellationToken cancellationToken)
  {
    var parts = presented.Split(':', 2);
    if (parts.Length != 2 || !Guid.TryParse(parts[0], out var id) || id == Guid.Empty || parts[1].Length == 0)
    {
      return null;
    }

    var now = clock.GetUtcNow();
    // The secret is checked against the remembered one too: knowing a credential's id alone never signs in. Only a
    // digest is kept, compared in constant time.
    if (cache.TryGetValue<(ValidServiceAccountCredential Valid, byte[] Digest)>(CacheKey(id), out var remembered))
    {
      if (remembered.Valid.ExpiresAt is not { } expiresAt || expiresAt > now)
      {
        if (!CryptographicOperations.FixedTimeEquals(remembered.Digest, Digest(parts[1])))
        {
          return null;
        }

        await RecordUseAsync(id, now, cancellationToken);
        return remembered.Valid;
      }

      cache.Remove(CacheKey(id));
    }

    var credential = await db.ServiceAccountCredentials.Include(x => x.ServiceAccount).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (credential?.ServiceAccount is not { IsEnabled: true } account || credential.IsDead(now))
    {
      return null;
    }

    var verified = hasher.VerifyHashedPassword(credential, credential.HashedSecret, parts[1]);
    if (verified == PasswordVerificationResult.Failed)
    {
      return null;
    }

    if (verified == PasswordVerificationResult.SuccessRehashNeeded)
    {
      credential.HashedSecret = hasher.HashPassword(credential, parts[1]);
    }

    credential.LastUsed = now;
    await db.SaveChangesAsync(cancellationToken);

    var valid = new ValidServiceAccountCredential(id, credential.ExpiresAt, account.Id, account.TenantId, account.Name);
    cache.Set(CacheKey(id), (valid, Digest(parts[1])), CacheFor);
    return valid;
  }

  /// <summary>
  /// Creates the configured server account and its credential, unrestricted, unless one by that name exists.
  /// Configuration naming only some of what it needs is a mistake worth stopping the server for.
  /// </summary>
  /// <returns>Whether the account was created now.</returns>
  public async Task<bool> BootstrapAsync(ServiceAccountBootstrapOptions options, CancellationToken cancellationToken)
  {
    var name = options.ServerServiceAccountName?.Trim();
    var secret = options.ServerServiceAccountCredentialSecret;
    var set = new[] { !string.IsNullOrEmpty(name), options.ServerServiceAccountCredentialId is not null, !string.IsNullOrEmpty(secret) };
    if (set.All(x => !x))
    {
      return false;
    }

    if (set.Any(x => !x))
    {
      throw new InvalidOperationException(
        $"Set {ServiceAccountBootstrapOptions.Section}:{nameof(options.ServerServiceAccountName)}, " +
        $"{nameof(options.ServerServiceAccountCredentialId)} and {nameof(options.ServerServiceAccountCredentialSecret)} together, or none of them.");
    }

    if (secret!.Length < ServiceAccountBootstrapOptions.MinimumSecretLength)
    {
      throw new InvalidOperationException(
        $"{ServiceAccountBootstrapOptions.Section}:{nameof(options.ServerServiceAccountCredentialSecret)} must be at least {ServiceAccountBootstrapOptions.MinimumSecretLength} characters.");
    }

    if (await NameTakenAsync(null, name!, except: null, cancellationToken))
    {
      return false;
    }

    var now = clock.GetUtcNow();
    var account = new ServiceAccountRecord
    {
      Id = options.ServerServiceAccountId ?? Guid.NewGuid(),
      Kind = ServiceAccountKind.Server,
      Name = name!,
      Description = Clean(options.ServerServiceAccountDescription),
      AccessMode = ServiceAccountAccessMode.Unrestricted,
      CreatedAt = now,
    };
    var credential = new ServiceAccountCredentialRecord
    {
      Id = options.ServerServiceAccountCredentialId!.Value,
      ServiceAccountId = account.Id,
      Name = "Bootstrap credential",
      CreatedAt = now,
    };
    credential.HashedSecret = hasher.HashPassword(credential, secret);
    db.ServiceAccounts.Add(account);
    db.ServiceAccountCredentials.Add(credential);
    db.AuthorizationChanges.Add(changes.Create(
      AuthorizationChangeActions.ServiceAccountCreated, actor: null, AuthorizationChangeTargets.ServiceAccount, account.Id, null,
      after: ServiceAccountSnapshot.Of(account)));
    db.AuthorizationChanges.Add(changes.Create(
      AuthorizationChangeActions.ServiceAccountCredentialCreated, actor: null, AuthorizationChangeTargets.ServiceAccountCredential, credential.Id, null,
      after: ServiceAccountCredentialSnapshot.Of(credential)));
    await db.SaveChangesAsync(cancellationToken);
    return true;
  }

  /// <summary>Forgets credentials that stopped working, e.g. when the cleanup deletes them.</summary>
  public void Forget(IEnumerable<ServiceAccountCredentialRecord> credentials)
  {
    foreach (var credential in credentials)
    {
      cache.Remove(CacheKey(credential.Id));
    }
  }

  /// <summary>Where an owner's accounts are found: <c>/api/v1/service-accounts</c>, or <c>.../server</c>.</summary>
  public static string Base(Guid? tenant) => tenant is null ? Routes.ServerServiceAccounts : Routes.ServiceAccounts;

  private IQueryable<ServiceAccountRecord> Accounts(Guid? tenant) => tenant is { } id
    ? db.ServiceAccounts.Where(x => x.Kind == ServiceAccountKind.Tenant && x.TenantId == id)
    : db.ServiceAccounts.Where(x => x.Kind == ServiceAccountKind.Server);

  private Task<ServiceAccountCredentialRecord?> CredentialAsync(Guid? tenant, Guid id, Guid credentialId, CancellationToken cancellationToken) =>
    db.ServiceAccountCredentials.FirstOrDefaultAsync(
      x => x.Id == credentialId && x.ServiceAccountId == id && Accounts(tenant).Any(a => a.Id == x.ServiceAccountId),
      cancellationToken);

  private Task<bool> NameTakenAsync(Guid? tenant, string name, Guid? except, CancellationToken cancellationToken) =>
    Accounts(tenant).AnyAsync(x => x.Name == name && x.Id != except, cancellationToken);

  // The check before saving can't stop two identical names saved at the same instant; the unique index can.
  private async Task<IResult?> SaveAsync(Guid? tenant, CancellationToken cancellationToken)
  {
    try
    {
      await db.SaveChangesAsync(cancellationToken);
      return null;
    }
    catch (DbUpdateException)
    {
      return NameConflict(tenant);
    }
  }

  // The bulk update can't run in memory, where the tracked row is changed instead.
  private async Task RecordUseAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
  {
    if (db.Database.IsRelational())
    {
      await db.ServiceAccountCredentials.Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(c => c.LastUsed, now), cancellationToken);
      return;
    }

    if (await db.ServiceAccountCredentials.FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is { } credential)
    {
      credential.LastUsed = now;
      await db.SaveChangesAsync(cancellationToken);
    }
  }

  private static string CacheKey(Guid credentialId) => $"service-account-credential:{credentialId}";

  private static byte[] Digest(string secret) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));

  private static string? DetailsRefusal(string? name, string? description)
  {
    if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > CreateServiceAccountRequest.NameMax)
    {
      return $"Enter a name of at most {CreateServiceAccountRequest.NameMax} characters.";
    }

    return description?.Trim().Length > CreateServiceAccountRequest.DescriptionMax
      ? $"The description can't be longer than {CreateServiceAccountRequest.DescriptionMax} characters."
      : null;
  }

  private static string? Clean(string? description) => string.IsNullOrWhiteSpace(description) ? null : description.Trim();

  private static IResult NameConflict(Guid? tenant) =>
    Results.Problem(tenant is null ? "A server service account with that name already exists." : "A service account with that name already exists in this tenant.", statusCode: StatusCodes.Status409Conflict);

  private static IResult Invalid(string reason) => Results.Problem(reason, statusCode: StatusCodes.Status400BadRequest);

  private static ServiceAccount ToContract(ServiceAccountRecord x) =>
    new(x.Id, x.Name, x.Description, x.Kind, x.IsEnabled, x.AccessMode, x.CreatedAt, [.. x.Credentials.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Select(ToContract)]);

  private static ServiceAccountCredential ToContract(ServiceAccountCredentialRecord x) =>
    new(x.Id, x.Name, x.CreatedAt, x.ExpiresAt, x.RevokedAt, x.LastUsed);
}
