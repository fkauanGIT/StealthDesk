using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>Keeps the server's errors, so tests can show a flow logs none.</summary>
public sealed class CapturedLogs : ILoggerProvider
{
  private readonly ConcurrentQueue<string> _errors = new();

  /// <summary>Every error or critical entry, as "Category: message".</summary>
  public IReadOnlyList<string> Errors => [.. _errors];

  public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _errors);

  public void Dispose()
  {
  }

  private sealed class Logger(string category, ConcurrentQueue<string> errors) : ILogger
  {
    public IDisposable? BeginScope<TState>(TState state)
      where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
      if (IsEnabled(logLevel))
      {
        errors.Enqueue($"{category}: {formatter(state, exception)}");
      }
    }
  }
}
