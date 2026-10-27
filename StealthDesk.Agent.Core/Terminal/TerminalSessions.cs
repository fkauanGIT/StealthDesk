using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StealthDesk.Agent.Core.Settings;
using StealthDesk.Contracts.Terminal;
using StealthDesk.Core;

namespace StealthDesk.Agent.Core.Terminal;

/// <summary>The terminal sessions running on this agent, by terminal id.</summary>
public interface ITerminalSessions
{
  /// <summary>Starts the session, which sends its banner and a prompt; a session already running is left as it is.</summary>
  Task<Outcome<Guid>> OpenAsync(Guid terminalId, CancellationToken cancellationToken = default);

  /// <summary>A line typed in the terminal: the answer to a question waiting, or the next command.</summary>
  Outcome<TerminalInputReceipt> Send(Guid terminalId, string line);

  /// <summary>Ends the session, stopping what it runs; nothing happens for one that isn't running.</summary>
  void Close(Guid terminalId);

  /// <summary>The ids of the sessions running.</summary>
  IReadOnlyCollection<Guid> Running { get; }
}

public sealed class TerminalSessions(
  ITerminalOutputSink sink,
  IAgentPaths paths,
  IOptions<TerminalOptions> options,
  TimeProvider clock,
  ILoggerFactory loggers) : ITerminalSessions, IAsyncDisposable, IDisposable
{
  private const string Ended = "The terminal session has ended.";

  private readonly ConcurrentDictionary<Guid, TerminalSession> _sessions = new();
  private readonly ILogger _logger = loggers.CreateLogger<TerminalSessions>();
  private readonly SemaphoreSlim _opening = new(1, 1);

  public IReadOnlyCollection<Guid> Running => [.. _sessions.Keys];

  public async Task<Outcome<Guid>> OpenAsync(Guid terminalId, CancellationToken cancellationToken = default)
  {
    // One at a time, so the same terminal opened twice at once gets one session.
    await _opening.WaitAsync(cancellationToken);
    try
    {
      if (_sessions.TryGetValue(terminalId, out var running) && !running.HasEnded)
      {
        return terminalId;
      }

      var session = new TerminalSession(terminalId, sink, options.Value, clock, loggers.CreateLogger<TerminalSession>(), Forget);
      try
      {
        var modules = PowerShellModules.Prepare(paths.InstanceFolder);
        _sessions[terminalId] = session;
        await session.StartAsync(AppContext.BaseDirectory, modules);
        _logger.LogInformation("Terminal {TerminalId} started.", terminalId);
        return terminalId;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Terminal {TerminalId} couldn't start.", terminalId);
        _sessions.TryRemove(terminalId, out _);
        await session.DisposeAsync();
        return Outcome.Failure<Guid>("PowerShell couldn't start on the device.");
      }
    }
    finally
    {
      _opening.Release();
    }
  }

  public Outcome<TerminalInputReceipt> Send(Guid terminalId, string line) =>
    _sessions.TryGetValue(terminalId, out var session) && session.Submit(line) is { } receipt
      ? receipt
      : Outcome.Failure<TerminalInputReceipt>(Ended);

  public void Close(Guid terminalId)
  {
    if (_sessions.TryGetValue(terminalId, out var session))
    {
      session.End("The session was closed.");
    }
  }

  public async ValueTask DisposeAsync()
  {
    foreach (var session in _sessions.Values)
    {
      await session.DisposeAsync();
    }

    _opening.Dispose();
  }

  // For a host disposed without waiting, as the command that joins a device to a server does: the sessions end, which
  // stops what they run, and let go of PowerShell in the background.
  public void Dispose()
  {
    foreach (var session in _sessions.Values)
    {
      session.End("The session was closed.");
    }
  }

  // A session that ended, whatever ended it, lets go of its runspace in the background.
  private void Forget(TerminalSession session)
  {
    _sessions.TryRemove(new KeyValuePair<Guid, TerminalSession>(session.Id, session));
    _ = Task.Run(async () =>
    {
      try
      {
        await session.DisposeAsync();
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Terminal {TerminalId} didn't close cleanly.", session.Id);
      }
    });
  }
}
