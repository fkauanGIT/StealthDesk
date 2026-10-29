using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StealthDesk.Core;

namespace StealthDesk.Agent.Core.DesktopClients;

/// <summary>
/// Keeps one desktop client running in each session that should have one: starts the missing ones, starts again the
/// ones that exited (less and less often while they keep exiting), stops the ones whose session ended, and stops any
/// other desktop client it didn't start, such as one left by an earlier run of the agent.
/// </summary>
public sealed class DesktopClientWatcher(
  IDesktopSessions sessions,
  IDesktopClientLauncher launcher,
  TimeProvider clock,
  ILogger<DesktopClientWatcher> logger) : BackgroundService
{
  public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

  /// <summary>A client that stayed up this long is healthy: if it exits later, that counts as its first failure.</summary>
  public static readonly TimeSpan HealthyAfter = TimeSpan.FromMinutes(1);

  /// <summary>How long a session waits before its client is started again, by how many times in a row it failed.</summary>
  public static readonly Backoff Restarts = new(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1), TimeSpan.Zero);

  private readonly Dictionary<int, Client> _clients = [];
  private readonly Dictionary<int, Retry> _retries = [];
  private bool _reportedMissing;

  /// <summary>Brings the clients in line with the sessions, once.</summary>
  public void Check()
  {
    var now = clock.GetUtcNow();
    var current = sessions.Current();
    var wanted = current.Select(x => x.Id).ToHashSet();

    foreach (var (sessionId, client) in _clients.ToList())
    {
      if (!wanted.Contains(sessionId))
      {
        logger.LogInformation("Session {SessionId} ended: stopping its desktop client (process {ProcessId}).", sessionId, client.Process.Id);
        Forget(sessionId, stop: true);
        _retries.Remove(sessionId);
      }
      else if (client.Process.HasExited)
      {
        var failures = (now - client.StartedAt >= HealthyAfter ? 0 : _retries.GetValueOrDefault(sessionId)?.Failures ?? 0) + 1;
        var wait = Restarts.DelayFor(failures);
        _retries[sessionId] = new Retry(failures, now + wait);
        logger.LogWarning(
          "The desktop client of session {SessionId} (process {ProcessId}) exited; starting it again in {Wait}.",
          sessionId,
          client.Process.Id,
          wait);
        Forget(sessionId, stop: false);
      }
      else if (now - client.StartedAt >= HealthyAfter)
      {
        _retries.Remove(sessionId);
      }
    }

    foreach (var sessionId in _retries.Keys.Where(x => !wanted.Contains(x)).ToList())
    {
      _retries.Remove(sessionId);
    }

    StopOthers();

    if (launcher.Missing() is { } missing)
    {
      if (!_reportedMissing)
      {
        logger.LogError("Desktop clients can't be started: {Reason}", missing);
        _reportedMissing = true;
      }

      return;
    }

    _reportedMissing = false;
    foreach (var session in current.Where(x => !_clients.ContainsKey(x.Id)))
    {
      var retry = _retries.GetValueOrDefault(session.Id);
      if (retry is not null && now < retry.NotBefore)
      {
        continue;
      }

      if (launcher.Start(session.Id) is { } process)
      {
        _clients[session.Id] = new Client(process, now);
        logger.LogInformation(
          "Started the desktop client of session {SessionId} ({User}) as process {ProcessId}.",
          session.Id,
          session.UserName ?? "sign-in screen",
          process.Id);
        continue;
      }

      var failures = (retry?.Failures ?? 0) + 1;
      var wait = Restarts.DelayFor(failures);
      _retries[session.Id] = new Retry(failures, now + wait);
      logger.LogWarning("The desktop client of session {SessionId} couldn't be started; trying again in {Wait}.", session.Id, wait);
    }
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    using var timer = new PeriodicTimer(CheckInterval, clock);
    try
    {
      do
      {
        try
        {
          Check();
        }
        catch (Exception ex)
        {
          logger.LogError(ex, "Checking the desktop clients failed.");
        }
      }
      while (await timer.WaitForNextTickAsync(stoppingToken));
    }
    catch (OperationCanceledException)
    {
      // Stopping.
    }
    finally
    {
      StopAll();
    }
  }

  /// <summary>Stops the clients it started: without the agent they have nobody to work for.</summary>
  public void StopAll()
  {
    foreach (var sessionId in _clients.Keys.ToList())
    {
      Forget(sessionId, stop: true);
    }

    _retries.Clear();
  }

  // Only one client per session, and only the agent's own.
  private void StopOthers()
  {
    var own = _clients.Values.Select(x => x.Process.Id).ToHashSet();
    foreach (var process in launcher.Running())
    {
      using (process)
      {
        if (!own.Contains(process.Id))
        {
          logger.LogWarning(
            "Stopping a desktop client this agent didn't start (process {ProcessId}, session {SessionId}).",
            process.Id,
            process.SessionId);
          process.Stop();
        }
      }
    }
  }

  private void Forget(int sessionId, bool stop)
  {
    var client = _clients[sessionId];
    _clients.Remove(sessionId);
    using (client.Process)
    {
      if (stop)
      {
        client.Process.Stop();
      }
    }
  }

  private sealed record Client(IDesktopClientProcess Process, DateTimeOffset StartedAt);

  private sealed record Retry(int Failures, DateTimeOffset NotBefore);
}
