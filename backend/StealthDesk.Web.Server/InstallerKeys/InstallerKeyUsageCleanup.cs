namespace StealthDesk.Web.Server.InstallerKeys;

/// <summary>Removes installs older than the history kept, when the server starts and then every 8 hours.</summary>
public sealed class InstallerKeyUsageCleanup(
  IServiceScopeFactory scopes,
  IOptionsMonitor<InstallerKeyOptions> options,
  TimeProvider clock,
  ILogger<InstallerKeyUsageCleanup> logger) : BackgroundService
{
  public static readonly TimeSpan Interval = TimeSpan.FromHours(8);

  /// <summary>Removes the installs past the history now; returns how many.</summary>
  public async Task<int> CleanAsync(CancellationToken cancellationToken = default)
  {
    var days = options.CurrentValue.UsageHistoryDays;
    if (days <= 0)
    {
      return 0;
    }

    var cutoff = clock.GetUtcNow() - TimeSpan.FromDays(days);
    await using var scope = scopes.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<StealthDeskDb>();
    var expired = db.InstallerKeyUsages.IgnoreQueryFilters().Where(x => x.UsedAt < cutoff);

    int removed;
    if (db.Database.IsInMemory())
    {
      // The in-memory provider can't delete in a single statement.
      db.InstallerKeyUsages.RemoveRange(await expired.ToListAsync(cancellationToken));
      removed = await db.SaveChangesAsync(cancellationToken);
    }
    else
    {
      removed = await expired.ExecuteDeleteAsync(cancellationToken);
    }

    if (removed > 0)
    {
      logger.LogInformation("Removed {Count} installer key usages older than {Cutoff}.", removed, cutoff);
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

  // An old install kept a while longer is harmless; stopping the server over it is not.
  private async Task TryCleanAsync(CancellationToken stoppingToken)
  {
    try
    {
      await CleanAsync(stoppingToken);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      logger.LogWarning(ex, "Could not remove old installer key usages; trying again in {Interval}.", Interval);
    }
  }
}
