using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Web.Server.AuthorizationLogs;

namespace StealthDesk.Web.Server.ServiceAccounts;

/// <summary>
/// Deletes credentials that were revoked or expired longer ago than the configured days, when the server starts and
/// then every 12 hours. Each deletion is logged, by the system.
/// </summary>
public sealed class ServiceAccountCredentialCleanup(
  IServiceScopeFactory scopes,
  IOptionsMonitor<ServiceAccountOptions> options,
  TimeProvider clock,
  ILogger<ServiceAccountCredentialCleanup> logger) : BackgroundService
{
  public static readonly TimeSpan Interval = TimeSpan.FromHours(12);

  // Deleted a batch at a time, each in a fresh context, so a large backlog never piles up in memory.
  private const int BatchSize = 200;

  /// <summary>Deletes the credentials past the cleanup now; returns how many.</summary>
  public async Task<int> CleanAsync(CancellationToken cancellationToken = default)
  {
    var days = options.CurrentValue.CredentialCleanupAfterDays;
    if (days <= 0)
    {
      return 0;
    }

    var cutoff = clock.GetUtcNow() - TimeSpan.FromDays(days);
    var removed = 0;
    while (true)
    {
      await using var scope = scopes.CreateAsyncScope();
      var db = scope.ServiceProvider.GetRequiredService<StealthDeskDb>();
      var changes = scope.ServiceProvider.GetRequiredService<IAuthorizationChangeFactory>();
      var batch = await db.ServiceAccountCredentials
        .Include(x => x.ServiceAccount)
        .Where(x => x.RevokedAt < cutoff || x.ExpiresAt < cutoff)
        .OrderBy(x => x.Id)
        .Take(BatchSize)
        .ToListAsync(cancellationToken);
      if (batch.Count == 0)
      {
        break;
      }

      foreach (var credential in batch)
      {
        db.AuthorizationChanges.Add(changes.Create(
          AuthorizationChangeActions.ServiceAccountCredentialDeleted,
          actor: null,
          AuthorizationChangeTargets.ServiceAccountCredential,
          credential.Id,
          credential.ServiceAccount?.TenantId,
          before: ServiceAccountCredentialSnapshot.Of(credential)));
      }

      db.ServiceAccountCredentials.RemoveRange(batch);
      await db.SaveChangesAsync(cancellationToken);
      scope.ServiceProvider.GetRequiredService<ServiceAccountService>().Forget(batch);
      removed += batch.Count;
    }

    if (removed > 0)
    {
      logger.LogInformation("Removed {Count} service account credentials revoked or expired before {Cutoff}.", removed, cutoff);
    }

    return removed;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    using var timer = new PeriodicTimer(Interval, clock);
    try
    {
      do
      {
        await TryCleanAsync(stoppingToken);
      }
      while (await timer.WaitForNextTickAsync(stoppingToken));
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
    }
  }

  // A dead credential kept a while longer is harmless; stopping the server over it is not.
  private async Task TryCleanAsync(CancellationToken stoppingToken)
  {
    try
    {
      await CleanAsync(stoppingToken);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      logger.LogWarning(ex, "Could not remove dead service account credentials; trying again in {Interval}.", Interval);
    }
  }
}
