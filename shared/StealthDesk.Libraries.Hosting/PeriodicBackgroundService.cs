using StealthDesk.Libraries.Shared.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StealthDesk.Libraries.Hosting;

public abstract class PeriodicBackgroundService(
  TimeSpan period,
  bool catchExceptions,
  TimeProvider timeProvider,
  ILogger<PeriodicBackgroundService> logger) : BackgroundService
{
  protected readonly ILogger<PeriodicBackgroundService> Logger = logger;

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    using var timer = new PeriodicTimer(period, timeProvider);

    try 
    {
      await OnStartingAsync(stoppingToken);
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
      Logger.LogInformation("Background service is stopping during startup due to cancellation.");
      return;
    }
    catch (Exception ex) when (catchExceptions)
    {
      // Log and fall through to the periodic loop so a transient startup failure
      // retries on the next tick instead of permanently disabling the service.
      Logger.LogError(ex, "Error during background service startup.");
    }

    while (await timer.WaitForNextTick(throwOnCancellation: false, stoppingToken))
    {
      try
      {
        await HandleElapsed(stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        Logger.LogInformation("Background service is stopping due to cancellation.");
        break;
      }
      catch (Exception ex) when (catchExceptions)
      {
        Logger.LogError(ex, "Error in periodic background service.");
      }
    }

    Logger.LogInformation("Stopping background service. Application is stopping.");
  }

  protected abstract Task HandleElapsed(CancellationToken stoppingToken);

  protected virtual Task OnStartingAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
