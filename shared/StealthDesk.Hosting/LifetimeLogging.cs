using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StealthDesk.Hosting;

/// <summary>Leaves a mark in the log when the process starts and when it begins shutting down.</summary>
public sealed class LifetimeLogging(IHostApplicationLifetime lifetime, ILogger<LifetimeLogging> logger) : IHostedService
{
  public Task StartAsync(CancellationToken cancellationToken)
  {
    lifetime.ApplicationStarted.Register(() =>
    {
      var entry = Assembly.GetEntryAssembly()?.GetName();
      logger.LogInformation("{Application} {Version} started.", entry?.Name, entry?.Version);
    });

    lifetime.ApplicationStopping.Register(() => logger.LogInformation("Shutting down."));
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
