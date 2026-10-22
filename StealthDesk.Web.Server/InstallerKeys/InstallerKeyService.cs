using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.InstallerKeys;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.ServiceAccounts;
using StealthDesk.Web.Server.AuthorizationLogs;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.InstallerKeys;

/// <summary>
/// A tenant's installer keys. Whoever lacks <c>installer-key.manage-all</c> sees and changes only the keys they made;
/// anyone else's is not found, never forbidden, so ids can't be probed. The agent is given <c>{key id}:{secret}</c>;
/// only a hash of the secret is kept.
/// </summary>
public sealed class InstallerKeyService(
  StealthDeskDb db,
  IPermissionEvaluator evaluator,
  IAuthorizationChangeFactory changes,
  IPasswordHasher<InstallerKeyRecord> hasher,
  InstallerKeyLocks locks,
  IOptionsMonitor<InstallerKeyOptions> options,
  TimeProvider clock,
  ILogger<InstallerKeyService> logger)
{
  private const string SecretAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  private const int SecretLength = 64;

  /// <summary>The keys that still work, with their installs within the history kept.</summary>
  public async Task<IReadOnlyList<InstallerKey>> ListAsync(Principal actor, Guid tenant, CancellationToken cancellationToken)
  {
    var keys = await Visible(actor, tenant, await ManagesAllAsync(actor, tenant, cancellationToken)).Include(x => x.DeviceGroup).AsNoTracking().ToListAsync(cancellationToken);
    var counts = await UsageCountsAsync([.. keys.Select(x => x.Id)], cancellationToken);
    var now = clock.GetUtcNow();
    var live = keys.Where(x => !x.IsSpent(counts.GetValueOrDefault(x.Id), now)).ToList();
    var names = await CreatorNamesAsync(live, cancellationToken);
    return [.. live
      .OrderByDescending(x => x.CreatedAt)
      .Select(x => ToContract(x, counts.GetValueOrDefault(x.Id), names.GetValueOrDefault((x.CreatorKind, x.CreatorId))))];
  }

  public async Task<IResult> CreateAsync(Principal actor, Guid tenant, CreateInstallerKeyRequest request, CancellationToken cancellationToken)
  {
    var name = Clean(request.Name);
    if (name?.Length > InstallerKeyRecord.NameMax)
    {
      return Invalid($"The name can't be longer than {InstallerKeyRecord.NameMax} characters.");
    }

    // A server-wide principal names the tenant; one that doesn't exist has nothing to install into.
    if (!await db.Tenants.AnyAsync(x => x.Id == tenant, cancellationToken))
    {
      return Results.NotFound();
    }

    // The devices the key brings in join its group: only one its creator may add devices to.
    if (request.DeviceGroupId is { } group
      && (!await db.DeviceGroups.IgnoreQueryFilters().AnyAsync(x => x.Id == group && x.TenantId == tenant, cancellationToken)
        || !(await evaluator.EvaluateAsync(actor, PermissionNames.DeviceGroupAssignDevices, Resource.DeviceGroup(group, tenant), cancellationToken)).Allowed))
    {
      return Invalid("Choose a device group of this tenant that you may add devices to.");
    }

    var now = clock.GetUtcNow();
    var key = new InstallerKeyRecord
    {
      Id = Guid.NewGuid(),
      TenantId = tenant,
      Name = name,
      Type = request.Type,
      CreatorKind = CreatorKindOf(actor),
      CreatorId = actor.Id,
      CreatedAt = now,
      DeviceGroupId = request.DeviceGroupId,
    };

    switch (request.Type)
    {
      case InstallerKeyType.UsageBased when request.AllowedUses is not > 0:
        return Invalid("A usage-based key needs at least one use.");
      case InstallerKeyType.UsageBased:
        key.AllowedUses = request.AllowedUses;
        key.ExpiresAt = now + InstallerKeyRules.UsageBasedLifetime;
        break;
      case InstallerKeyType.TimeBased when request.ExpiresAt is not { } expiresAt || expiresAt <= now:
        return Invalid("A time-based key must expire in the future.");
      case InstallerKeyType.TimeBased:
        key.ExpiresAt = request.ExpiresAt;
        break;
      case InstallerKeyType.Persistent:
        break;
      default:
        return Invalid("Choose a usage-based, time-based or persistent key.");
    }

    var secret = RandomNumberGenerator.GetString(SecretAlphabet, SecretLength);
    key.HashedSecret = hasher.HashPassword(key, secret);
    db.InstallerKeys.Add(key);
    db.AuthorizationChanges.Add(changes.Create(
      AuthorizationChangeActions.InstallerKeyCreated, actor, AuthorizationChangeTargets.InstallerKey, key.Id, tenant,
      after: InstallerKeySnapshot.Of(key)));
    await db.SaveChangesAsync(cancellationToken);
    await db.Entry(key).Reference(x => x.DeviceGroup).LoadAsync(cancellationToken);

    var names = await CreatorNamesAsync([key], cancellationToken);
    return Results.Created(
      Routes.InstallerKey(key.Id),
      new CreatedInstallerKey(ToContract(key, 0, names.GetValueOrDefault((key.CreatorKind, key.CreatorId))), $"{key.Id}:{secret}"));
  }

  public async Task<IResult> RenameAsync(Principal actor, Guid tenant, Guid id, string? newName, CancellationToken cancellationToken)
  {
    var name = Clean(newName);
    if (name?.Length > InstallerKeyRecord.NameMax)
    {
      return Invalid($"The name can't be longer than {InstallerKeyRecord.NameMax} characters.");
    }

    if (await FindAsync(actor, tenant, id, cancellationToken) is not { } key)
    {
      return Results.NotFound();
    }

    var before = InstallerKeySnapshot.Of(key);
    key.Name = name;
    db.AuthorizationChanges.Add(changes.Create(
      AuthorizationChangeActions.InstallerKeyRenamed, actor, AuthorizationChangeTargets.InstallerKey, id, tenant,
      before, InstallerKeySnapshot.Of(key)));
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
  }

  /// <summary>Revokes the key: it is deleted with its installs. Devices it brought in keep working.</summary>
  public async Task<IResult> DeleteAsync(Principal actor, Guid tenant, Guid id, CancellationToken cancellationToken)
  {
    if (await FindAsync(actor, tenant, id, cancellationToken) is not { } key)
    {
      return Results.NotFound();
    }

    db.AuthorizationChanges.Add(changes.Create(
      AuthorizationChangeActions.InstallerKeyDeleted, actor, AuthorizationChangeTargets.InstallerKey, id, tenant,
      before: InstallerKeySnapshot.Of(key)));
    db.InstallerKeys.Remove(key);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
  }

  /// <summary>The installs made with the key, within the history kept, latest first.</summary>
  public async Task<IResult> UsagesAsync(Principal actor, Guid tenant, Guid id, CancellationToken cancellationToken)
  {
    if (await FindAsync(actor, tenant, id, cancellationToken) is null)
    {
      return Results.NotFound();
    }

    var cutoff = HistoryCutoff();
    var usages = await db.InstallerKeyUsages
      .AsNoTracking()
      .Where(x => x.InstallerKeyId == id && (cutoff == null || x.UsedAt >= cutoff))
      .OrderByDescending(x => x.UsedAt)
      .Select(x => new InstallerKeyUsage(x.Id, x.DeviceId, x.UsedAt, x.RemoteAddress))
      .ToListAsync(cancellationToken);
    return Results.Ok(new InstallerKeyUsageList { Items = usages });
  }

  /// <summary>
  /// The key an <c>id:secret</c> stands for, when its secret matches and it still works; nothing is used up. A key
  /// found spent is deleted, by the system.
  /// </summary>
  public async Task<InstallerKeyRecord?> CheckAsync(string presented, CancellationToken cancellationToken)
  {
    if (Parse(presented) is not var (id, secret))
    {
      return null;
    }

    var key = await db.InstallerKeys.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (key is null || !Matches(key, secret))
    {
      return null;
    }

    var uses = key.Type == InstallerKeyType.UsageBased ? await CountUsesAsync(id, cancellationToken) : 0;
    if (key.IsSpent(uses, clock.GetUtcNow()))
    {
      await RemoveSpentAsync(key, cancellationToken);
      return null;
    }

    return key;
  }

  /// <summary>
  /// Uses the key for one install of <paramref name="deviceId"/>, recording where it came from. A usage-based key
  /// is deleted once its last use is gone. False when the key stopped working since it was checked.
  /// </summary>
  public async Task<bool> RedeemAsync(Guid keyId, Guid deviceId, string? remoteAddress, CancellationToken cancellationToken)
  {
    // Counting the uses and adding one happen under a lock per key: two agents installing at once can't both take
    // the last use.
    using var held = await locks.AcquireAsync(keyId, cancellationToken);

    var key = await db.InstallerKeys.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == keyId, cancellationToken);
    if (key is null)
    {
      return false;
    }

    var uses = key.Type == InstallerKeyType.UsageBased ? await CountUsesAsync(keyId, cancellationToken) : 0;
    var now = clock.GetUtcNow();
    if (key.IsSpent(uses, now))
    {
      await RemoveSpentAsync(key, cancellationToken);
      return false;
    }

    db.InstallerKeyUsages.Add(new InstallerKeyUsageRecord
    {
      Id = Guid.NewGuid(),
      InstallerKeyId = keyId,
      TenantId = key.TenantId,
      DeviceId = deviceId,
      RemoteAddress = remoteAddress?.Length > InstallerKeyUsageRecord.RemoteAddressMax ? null : remoteAddress,
      UsedAt = now,
    });
    await db.SaveChangesAsync(cancellationToken);

    if (key.IsSpent(uses + 1, now))
    {
      await RemoveSpentAsync(key, cancellationToken);
    }

    return true;
  }

  /// <summary>
  /// Puts a device that just joined with a key in the key's group, logged as the system's doing; a group deleted in
  /// the meantime leaves it in none.
  /// </summary>
  public async Task PlaceAsync(DeviceRecord device, Guid group, Guid? keyId, CancellationToken cancellationToken)
  {
    if (device.GroupMemberships.Any(x => x.DeviceGroupId == group)
      || !await db.DeviceGroups.IgnoreQueryFilters().AnyAsync(x => x.Id == group && x.TenantId == device.TenantId, cancellationToken))
    {
      return;
    }

    device.GroupMemberships.Add(new DeviceGroupMemberRecord { DeviceGroupId = group, DeviceId = device.Id });
    db.AuthorizationChanges.Add(changes.Create(
      AuthorizationChangeActions.DeviceGroupMembersAdded, actor: null, AuthorizationChangeTargets.DeviceGroup, group, device.TenantId,
      after: new DeviceGroupMembershipChange(1, InstallerKeyId: keyId)));
    await db.SaveChangesAsync(cancellationToken);
  }

  /// <summary>
  /// Whether the key's creator may put a new agent on a device that already exists: a user or tenant service account
  /// with <c>agent.install</c> on it, or any enabled server service account.
  /// </summary>
  public async Task<bool> MayReinstallAsync(InstallerKeyRecord key, DeviceRecord device, CancellationToken cancellationToken)
  {
    var tenant = key.TenantId;
    Principal creator;
    switch (key.CreatorKind)
    {
      case InstallerKeyCreatorKind.User:
        if (!await db.Users.IgnoreQueryFilters().AnyAsync(x => x.Id == key.CreatorId && x.TenantId == tenant, cancellationToken))
        {
          return false;
        }

        creator = new Principal(PermissionPrincipalKind.User, key.CreatorId, tenant);
        break;
      case InstallerKeyCreatorKind.TenantServiceAccount:
        if (!await db.ServiceAccounts.AnyAsync(x => x.Id == key.CreatorId && x.Kind == ServiceAccountKind.Tenant && x.TenantId == tenant && x.IsEnabled, cancellationToken))
        {
          return false;
        }

        creator = new Principal(PermissionPrincipalKind.ServiceAccount, key.CreatorId, tenant);
        break;
      case InstallerKeyCreatorKind.ServerServiceAccount:
        // Server accounts work across tenants by design; a tenant account's id must never pass for one.
        return await db.ServiceAccounts.AnyAsync(x => x.Id == key.CreatorId && x.Kind == ServiceAccountKind.Server && x.IsEnabled, cancellationToken);
      default:
        return false;
    }

    return (await evaluator.EvaluateAsync(creator, PermissionNames.AgentInstall, Resource.Device(device), cancellationToken)).Allowed;
  }

  // Server principals manage every key of the tenant they act in.
  private async Task<bool> ManagesAllAsync(Principal actor, Guid tenant, CancellationToken cancellationToken) =>
    actor.TenantId is null
    || (await evaluator.EvaluateAsync(actor, PermissionNames.InstallerKeyManageAll, Resource.Tenant(tenant), cancellationToken)).Allowed;

  private IQueryable<InstallerKeyRecord> Visible(Principal actor, Guid tenant, bool all)
  {
    var keys = db.InstallerKeys.IgnoreQueryFilters().Where(x => x.TenantId == tenant);
    if (all)
    {
      return keys;
    }

    var kind = CreatorKindOf(actor);
    return keys.Where(x => x.CreatorKind == kind && x.CreatorId == actor.Id);
  }

  private async Task<InstallerKeyRecord?> FindAsync(Principal actor, Guid tenant, Guid id, CancellationToken cancellationToken) =>
    await Visible(actor, tenant, await ManagesAllAsync(actor, tenant, cancellationToken)).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

  private async Task RemoveSpentAsync(InstallerKeyRecord key, CancellationToken cancellationToken)
  {
    db.AuthorizationChanges.Add(changes.Create(
      AuthorizationChangeActions.InstallerKeyDeleted, actor: null, AuthorizationChangeTargets.InstallerKey, key.Id, key.TenantId,
      before: InstallerKeySnapshot.Of(key)));
    db.InstallerKeys.Remove(key);
    try
    {
      await db.SaveChangesAsync(cancellationToken);
    }
    catch (DbUpdateConcurrencyException)
    {
      // Another request removed it first; it's gone either way.
      db.ChangeTracker.Clear();
      logger.LogDebug("Installer key {KeyId} was already removed.", key.Id);
    }
  }

  private bool Matches(InstallerKeyRecord key, string secret)
  {
    var verified = hasher.VerifyHashedPassword(key, key.HashedSecret, secret);
    if (verified == PasswordVerificationResult.SuccessRehashNeeded)
    {
      key.HashedSecret = hasher.HashPassword(key, secret);
    }

    return verified != PasswordVerificationResult.Failed;
  }

  // Every use counts toward a usage-based key's limit, even past the history shown.
  private Task<int> CountUsesAsync(Guid keyId, CancellationToken cancellationToken) =>
    db.InstallerKeyUsages.IgnoreQueryFilters().CountAsync(x => x.InstallerKeyId == keyId, cancellationToken);

  private async Task<Dictionary<Guid, int>> UsageCountsAsync(Guid[] keyIds, CancellationToken cancellationToken)
  {
    var cutoff = HistoryCutoff();
    return await db.InstallerKeyUsages
      .IgnoreQueryFilters()
      .Where(x => keyIds.Contains(x.InstallerKeyId) && (cutoff == null || x.UsedAt >= cutoff))
      .GroupBy(x => x.InstallerKeyId)
      .Select(x => new { x.Key, Count = x.Count() })
      .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
  }

  private async Task<Dictionary<(InstallerKeyCreatorKind, Guid), string>> CreatorNamesAsync(IReadOnlyList<InstallerKeyRecord> keys, CancellationToken cancellationToken)
  {
    var users = keys.Where(x => x.CreatorKind == InstallerKeyCreatorKind.User).Select(x => x.CreatorId).Distinct().ToList();
    var accounts = keys.Where(x => x.CreatorKind != InstallerKeyCreatorKind.User).Select(x => x.CreatorId).Distinct().ToList();
    var names = new Dictionary<(InstallerKeyCreatorKind, Guid), string>();

    foreach (var user in await db.Users.IgnoreQueryFilters().Where(x => users.Contains(x.Id)).Select(x => new { x.Id, x.Email }).ToListAsync(cancellationToken))
    {
      names[(InstallerKeyCreatorKind.User, user.Id)] = user.Email ?? string.Empty;
    }

    foreach (var account in await db.ServiceAccounts.Where(x => accounts.Contains(x.Id)).Select(x => new { x.Id, x.Kind, x.Name }).ToListAsync(cancellationToken))
    {
      var kind = account.Kind == ServiceAccountKind.Server ? InstallerKeyCreatorKind.ServerServiceAccount : InstallerKeyCreatorKind.TenantServiceAccount;
      names[(kind, account.Id)] = account.Name;
    }

    return names;
  }

  private DateTimeOffset? HistoryCutoff()
  {
    var days = options.CurrentValue.UsageHistoryDays;
    return days <= 0 ? null : clock.GetUtcNow() - TimeSpan.FromDays(days);
  }

  private static (Guid Id, string Secret)? Parse(string presented)
  {
    var parts = presented.Split(':', 2);
    return parts.Length == 2 && Guid.TryParse(parts[0], out var id) && id != Guid.Empty && parts[1].Length > 0
      ? (id, parts[1])
      : null;
  }

  private static InstallerKeyCreatorKind CreatorKindOf(Principal actor) => actor switch
  {
    { Kind: PermissionPrincipalKind.ServiceAccount, TenantId: null } => InstallerKeyCreatorKind.ServerServiceAccount,
    { Kind: PermissionPrincipalKind.ServiceAccount } => InstallerKeyCreatorKind.TenantServiceAccount,
    _ => InstallerKeyCreatorKind.User,
  };

  private static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

  private static IResult Invalid(string reason) => Results.Problem(reason, statusCode: StatusCodes.Status400BadRequest);

  private static InstallerKey ToContract(InstallerKeyRecord x, int usageCount, string? creatorName) =>
    new(x.Id, x.Name, x.Type, x.CreatorKind, x.CreatorId, creatorName, x.CreatedAt, x.AllowedUses, x.ExpiresAt, usageCount, x.DeviceGroupId, x.DeviceGroup?.Name);
}

/// <summary>One lock per installer key, held while a use is counted and added.</summary>
public sealed class InstallerKeyLocks
{
  private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

  public async Task<IDisposable> AcquireAsync(Guid keyId, CancellationToken cancellationToken)
  {
    var gate = _locks.GetOrAdd(keyId, _ => new SemaphoreSlim(1, 1));
    await gate.WaitAsync(cancellationToken);
    return new Release(gate);
  }

  private sealed class Release(SemaphoreSlim gate) : IDisposable
  {
    private int _released;

    public void Dispose()
    {
      if (Interlocked.Exchange(ref _released, 1) == 0)
      {
        gate.Release();
      }
    }
  }
}
