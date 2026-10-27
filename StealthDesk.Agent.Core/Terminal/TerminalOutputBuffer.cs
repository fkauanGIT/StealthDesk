using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.Terminal;

namespace StealthDesk.Agent.Core.Terminal;

/// <summary>Where a terminal session's output goes: towards the browser that opened it.</summary>
public interface ITerminalOutputSink
{
  Task SendAsync(TerminalOutput output, CancellationToken cancellationToken);
}

/// <summary>
/// Gathers what a session writes and sends it in order. PowerShell writes line by line, often thousands of lines for
/// one command; sending each would mean a message per line, so writes of the same kind are joined and sent shortly
/// after, cut into pieces that fit in a message.
/// </summary>
internal sealed class TerminalOutputBuffer : IAsyncDisposable
{
  // Long enough to join a burst of lines, short enough to look immediate.
  private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(100);

  private readonly Guid _terminalId;
  private readonly ITerminalOutputSink _sink;
  private readonly ILogger _logger;
  private readonly Channel<TerminalOutput> _queue = Channel.CreateUnbounded<TerminalOutput>(new UnboundedChannelOptions { SingleReader = true });
  private readonly Lock _gate = new();
  private readonly StringBuilder _pending = new();
  private readonly ITimer _flushTimer;
  private readonly Task _sending;
  private TerminalOutputKind _pendingKind;
  private bool _ended;

  public TerminalOutputBuffer(Guid terminalId, ITerminalOutputSink sink, TimeProvider clock, ILogger logger)
  {
    _terminalId = terminalId;
    _sink = sink;
    _logger = logger;
    _flushTimer = clock.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    _sending = Task.Run(SendQueuedAsync);
  }

  public void Write(TerminalOutputKind kind, string text)
  {
    if (string.IsNullOrEmpty(text))
    {
      return;
    }

    lock (_gate)
    {
      if (_ended)
      {
        return;
      }

      if (_pending.Length > 0 && kind != _pendingKind)
      {
        QueuePending();
      }

      var startsBatch = _pending.Length == 0;
      _pendingKind = kind;
      _pending.Append(text);
      if (_pending.Length >= TerminalOutput.MaxTextBytes)
      {
        QueuePending();
      }
      else if (startsBatch)
      {
        _flushTimer.Change(FlushDelay, Timeout.InfiniteTimeSpan);
      }
    }
  }

  /// <summary>Sends what was written so far without waiting, e.g. before waiting for an answer.</summary>
  public void Flush()
  {
    lock (_gate)
    {
      QueuePending();
    }
  }

  /// <summary>Empties the screen: what wasn't sent yet would be cleared anyway, so it is dropped.</summary>
  public void Clear()
  {
    lock (_gate)
    {
      if (_ended)
      {
        return;
      }

      _flushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
      _pending.Clear();
      _queue.Writer.TryWrite(new TerminalOutput(_terminalId, TerminalOutputKind.Cleared, string.Empty));
    }
  }

  /// <summary>Sends what is left and then the end of the session; nothing written afterwards is sent.</summary>
  public void End(string reason)
  {
    lock (_gate)
    {
      if (_ended)
      {
        return;
      }

      QueuePending();
      _queue.Writer.TryWrite(new TerminalOutput(_terminalId, TerminalOutputKind.Ended, reason));
      _ended = true;
      _queue.Writer.TryComplete();
    }
  }

  public async ValueTask DisposeAsync()
  {
    lock (_gate)
    {
      _ended = true;
      _queue.Writer.TryComplete();
    }

    await _flushTimer.DisposeAsync();
    await _sending;
  }

  // Under _gate.
  private void QueuePending()
  {
    _flushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    if (_pending.Length == 0)
    {
      return;
    }

    foreach (var piece in Pieces(_pending.ToString()))
    {
      _queue.Writer.TryWrite(new TerminalOutput(_terminalId, _pendingKind, piece));
    }

    _pending.Clear();
  }

  private async Task SendQueuedAsync()
  {
    await foreach (var output in _queue.Reader.ReadAllAsync())
    {
      try
      {
        await _sink.SendAsync(output, CancellationToken.None);
      }
      catch (Exception ex)
      {
        // The browser may be gone; the session goes on, as a console does when nobody looks at it.
        _logger.LogWarning(ex, "Couldn't send output of terminal {TerminalId}.", _terminalId);
      }
    }
  }


  /// <summary>The text cut into pieces of at most <see cref="TerminalOutput.MaxTextBytes"/>, never inside a character.</summary>
  internal static IEnumerable<string> Pieces(string text)
  {
    var start = 0;
    var bytes = 0;
    for (var i = 0; i < text.Length;)
    {
      var width = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
      var size = Encoding.UTF8.GetByteCount(text.AsSpan(i, width));
      if (bytes + size > TerminalOutput.MaxTextBytes)
      {
        yield return text[start..i];
        start = i;
        bytes = 0;
      }

      bytes += size;
      i += width;
    }

    if (start < text.Length)
    {
      yield return text[start..];
    }
  }
}
