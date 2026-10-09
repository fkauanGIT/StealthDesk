namespace StealthDesk.Web.Server.AuthorizationLogs;

public sealed class AuthorizationLogOptions
{
  public const string Section = "AuthorizationLogs";

  /// <summary>Entries older than this are removed once a day. Zero or less keeps every entry.</summary>
  public int RetentionDays { get; set; } = 365;
}

/// <summary>Removes change log entries older than the retention, when the server starts and then once a day.</summary>
public sealed class AuthorizationLogCleanup(
  IServiceScopeFactory scopes,
  IOptionsMonitor<AuthorizationLogOptions> options,
  TimeProvider clock,
  ILogger<AuthorizationLogCleanup> logger) : BackgroundService
{
  public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

  /// <summary>Removes the expired entries now; returns how many.</summary>
  public async Task<int> CleanAsync(CancellationToken cancellationToken = default)
  {
    var days = options.CurrentValue.RetentionDays;
    if (days <= 0)
    {
      return 0;
    }

    var cutoff = clock.GetUtcNow() - TimeSpan.FromDays(days);
    await using var scope = scopes.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<StealthDeskDb>();
    var expired = db.AuthorizationChanges.Where(x => x.CreatedAt < cutoff);

    int removed;
    if (db.Database.IsInMemory())
    {
      // The in-memory provider can't delete in a single statement.
      db.AuthorizationChanges.RemoveRange(await expired.ToListAsync(cancellationToken));
      removed = await db.SaveChangesAsync(cancellationToken);
    }
    else
    {
      removed = await expired.ExecuteDeleteAsync(cancellationToken);
    }

    if (removed > 0)
    {
      logger.LogInformation("Removed {Count} authorization change log entries older than {Cutoff}.", removed, cutoff);
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

  // Keeping entries a day longer is harmless; stopping the server over it is not.
  private async Task TryCleanAsync(CancellationToken stoppingToken)
  {
    try
    {
      await CleanAsync(stoppingToken);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      logger.LogWarning(ex, "Could not remove expired authorization change log entries; trying again in {Interval}.", Interval);
    }
  }
}
