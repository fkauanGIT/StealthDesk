using MessagePack;

namespace StealthDesk.Contracts.Terminal;

public enum TerminalOutputKind
{
  /// <summary>Text as PowerShell's console shows it: results, the prompt, warnings and the like.</summary>
  Output,

  /// <summary>Errors.</summary>
  Error,

  /// <summary>The session is over; the text says why. Nothing follows it.</summary>
  Ended,
}

/// <summary>
/// A piece of what a terminal session shows, in the order it was written. Pieces are not lines: the text goes on where
/// the previous piece stopped.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TerminalOutput(Guid TerminalId, TerminalOutputKind Kind, string Text)
{
  /// <summary>
  /// The most UTF-8 bytes of text in one piece, well under the 32 KB a realtime message may carry by default, so the
  /// rest of the message always fits.
  /// </summary>
  public const int MaxTextBytes = 16 * 1024;
}
