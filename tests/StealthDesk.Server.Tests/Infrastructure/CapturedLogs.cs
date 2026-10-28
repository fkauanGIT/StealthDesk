using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>
/// Keeps the server's warnings and errors, so tests can show a flow logs none, or one it must; from a lower level when
/// asked, e.g. to show something is never logged at all.
/// </summary>
public sealed class CapturedLogs(LogLevel minimum = LogLevel.Warning) : ILoggerProvider
{
  private readonly ConcurrentQueue<(LogLevel Level, string Text)> _entries = new();

  /// <summary>Every error or critical entry, as "Category: message".</summary>
  public IReadOnlyList<string> Errors => [.. _entries.Where(x => x.Level >= LogLevel.Error).Select(x => x.Text)];

  /// <summary>Every warning, as "Category: message".</summary>
  public IReadOnlyList<string> Warnings => [.. _entries.Where(x => x.Level == LogLevel.Warning).Select(x => x.Text)];

  /// <summary>Every entry kept, as "Category: message".</summary>
  public IReadOnlyList<string> All => [.. _entries.Select(x => x.Text)];

  public ILogger CreateLogger(string categoryName) => new Logger(categoryName, minimum, _entries);

  public void Dispose()
  {
  }

  private sealed class Logger(string category, LogLevel minimum, ConcurrentQueue<(LogLevel Level, string Text)> entries) : ILogger
  {
    public IDisposable? BeginScope<TState>(TState state)
      where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
      if (IsEnabled(logLevel))
      {
        entries.Enqueue((logLevel, $"{category}: {formatter(state, exception)}"));
      }
    }
  }
}
