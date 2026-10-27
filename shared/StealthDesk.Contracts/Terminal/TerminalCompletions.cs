using MessagePack;

namespace StealthDesk.Contracts.Terminal;

/// <summary>What a completion is: a command, a parameter, a path... as PowerShell sorts them.</summary>
public enum TerminalCompletionKind
{
  Text,
  History,
  Command,
  ProviderItem,
  ProviderContainer,
  Property,
  Method,
  ParameterName,
  ParameterValue,
  Variable,
  Namespace,
  Type,
  Keyword,
  DynamicKeyword,
}

/// <summary>
/// Completions asked for what is typed, with the caret at <see cref="Cursor"/>. With <see cref="Forward"/> set, the next
/// match (Tab) or the one before (Shift+Tab) of the same input; without it, a page of every match.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TerminalCompletionRequest(string Input, int Cursor, bool? Forward, int Page = 0)
{
  /// <summary>Matches per page when listing them all.</summary>
  public const int PageSize = 50;
}

/// <param name="CompletionText">What goes in the input.</param>
/// <param name="ListItemText">How the match shows in a list.</param>
/// <param name="ToolTip">More about it, e.g. a command's syntax.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TerminalCompletionMatch(string CompletionText, string ListItemText, TerminalCompletionKind Kind, string ToolTip);

/// <summary>
/// The matches, which replace <see cref="ReplacementLength"/> characters of the input from <see cref="ReplacementIndex"/>.
/// Listing more than <see cref="MaxListed"/> matches answers their count without them.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TerminalCompletions(
  int ReplacementIndex,
  int ReplacementLength,
  IReadOnlyList<TerminalCompletionMatch> Matches,
  int TotalCount,
  int Page,
  bool HasMorePages)
{
  /// <summary>The most matches listed; more than that are too many to read, so typing more is asked for.</summary>
  public const int MaxListed = 500;
}
