using MessagePack;

namespace StealthDesk.Contracts.Terminal;

/// <summary>
/// A line a terminal took: a command, or the answer to a question a command asked. Only commands belong in the
/// history of what was typed; an answer may be a password.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TerminalInputReceipt(Guid TerminalId, bool AnsweredQuestion);
