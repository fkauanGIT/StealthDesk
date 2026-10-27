using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Security;
using System.Text;
using StealthDesk.Agent.Core.Inventory;
using StealthDesk.Contracts.Terminal;

namespace StealthDesk.Agent.Core.Terminal;

/// <summary>What the host needs from its session: somewhere to write, and someone to ask.</summary>
internal interface ITerminalConsole
{
  void Write(TerminalOutputKind kind, string text);

  /// <summary>
  /// Waits for the next line typed in the terminal and shows it, as a console does; null when nobody answered in
  /// time. Throws <see cref="PromptingException"/> when the session ends.
  /// </summary>
  /// <param name="secret">A password or other secret: shown as one <c>*</c> per character.</param>
  string? ReadLine(bool secret = false);

  /// <summary>The script ran <c>exit</c>.</summary>
  void RequestExit(int exitCode);

  /// <summary>The script cleared the screen (<c>cls</c>).</summary>
  void Clear();
}

/// <summary>
/// The PowerShell host of a terminal session: PowerShell asks it to show text and to ask the user things, as it asks
/// a console window; here both go through the terminal in the browser.
/// </summary>
internal sealed class TerminalHost(ITerminalConsole console) : PSHost
{
  private readonly TerminalHostUserInterface _ui = new(console);

  public override CultureInfo CurrentCulture => CultureInfo.CurrentCulture;
  public override CultureInfo CurrentUICulture => CultureInfo.CurrentUICulture;
  public override Guid InstanceId { get; } = Guid.NewGuid();
  public override string Name => "StealthDesk Terminal";
  public override PSHostUserInterface UI => _ui;
  public override Version Version => Version.TryParse(PortableInventory.AgentVersion, out var version) ? version : new Version(0, 0);

  // Nested prompts (debugging, [S]uspend in a confirmation) would need a console the browser doesn't have.
  public override void EnterNestedPrompt() => throw new NotSupportedException("The terminal has no nested prompts.");
  public override void ExitNestedPrompt() => throw new NotSupportedException("The terminal has no nested prompts.");
  public override void NotifyBeginApplication() { }
  public override void NotifyEndApplication() { }
  public override void SetShouldExit(int exitCode) => console.RequestExit(exitCode);
}

internal sealed class TerminalHostUserInterface(ITerminalConsole console) : PSHostUserInterface
{
  private readonly TerminalRawUserInterface _raw = new(console);
  private readonly Dictionary<long, string> _lastProgress = [];

  public override PSHostRawUserInterface RawUI => _raw;

  public override void Write(string value) => console.Write(TerminalOutputKind.Output, value);
  public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) => Write(value);
  public override void WriteLine(string value) => Write(value + "\n");
  public override void WriteErrorLine(string value) => console.Write(TerminalOutputKind.Error, value + "\n");
  public override void WriteWarningLine(string message) => WriteLine($"WARNING: {message}");
  public override void WriteVerboseLine(string message) => WriteLine($"VERBOSE: {message}");
  public override void WriteDebugLine(string message) => WriteLine($"DEBUG: {message}");

  // A progress bar redrawn in place has no equivalent in lines of text: each change of step or percentage is a line,
  // and repeats are left out, so a download doesn't print thousands of them.
  public override void WriteProgress(long sourceId, ProgressRecord record)
  {
    if (record.RecordType == ProgressRecordType.Completed)
    {
      _lastProgress.Remove(sourceId);
      return;
    }

    var line = record.PercentComplete >= 0
      ? $"{record.Activity}: {record.StatusDescription} ({record.PercentComplete}%)"
      : $"{record.Activity}: {record.StatusDescription}";
    if (_lastProgress.GetValueOrDefault(sourceId) == line)
    {
      return;
    }

    _lastProgress[sourceId] = line;
    WriteLine(line);
  }

  // A question nobody answered goes on as if Enter was pressed.
  public override string ReadLine() => console.ReadLine() ?? string.Empty;

  public override SecureString ReadLineAsSecureString() => Secure(console.ReadLine(secret: true) ?? string.Empty);

  public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions)
  {
    WriteHeading(caption, message);
    var answers = new Dictionary<string, PSObject>();
    foreach (var field in descriptions)
    {
      var label = string.IsNullOrEmpty(field.Label) ? field.Name : field.Label.Replace("&", string.Empty);
      var type = Type.GetType(field.ParameterAssemblyFullName) ?? typeof(string);
      if (type == typeof(PSCredential))
      {
        answers[field.Name] = new PSObject(PromptForCredential(caption, message, string.Empty, string.Empty));
        continue;
      }

      Write($"{label}: ");
      if (type == typeof(SecureString))
      {
        answers[field.Name] = new PSObject(ReadLineAsSecureString());
        continue;
      }

      var answer = ReadLine();
      answers[field.Name] = new PSObject(LanguagePrimitives.TryConvertTo(answer, type, CultureInfo.InvariantCulture, out var converted) ? converted : answer);
    }

    return answers;
  }

  public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice)
  {
    WriteHeading(caption, message);
    var keys = choices.Select(HotKey).ToList();
    var options = new StringBuilder();
    for (var i = 0; i < choices.Count; i++)
    {
      options.Append($"[{keys[i]}] {choices[i].Label.Replace("&", string.Empty)}  ");
    }

    options.Append("[?] Help");
    if (defaultChoice >= 0 && defaultChoice < choices.Count)
    {
      options.Append($" (default is \"{keys[defaultChoice]}\")");
    }

    // Asked again until the answer is one of the choices, as the console does; nobody answering gets the default,
    // or no choice (-1) when there is none, rather than the question again.
    while (true)
    {
      Write($"{options}: ");
      if (console.ReadLine() is not { } typed)
      {
        return defaultChoice;
      }

      var answer = typed.Trim();
      if (answer.Length == 0 && defaultChoice >= 0 && defaultChoice < choices.Count)
      {
        return defaultChoice;
      }

      if (answer == "?")
      {
        for (var i = 0; i < choices.Count; i++)
        {
          WriteLine($"{keys[i]} - {(string.IsNullOrEmpty(choices[i].HelpMessage) ? choices[i].Label.Replace("&", string.Empty) : choices[i].HelpMessage)}");
        }

        continue;
      }

      var chosen = keys.FindIndex(x => string.Equals(x, answer, StringComparison.OrdinalIgnoreCase));
      if (chosen < 0)
      {
        chosen = choices.ToList().FindIndex(x => string.Equals(x.Label.Replace("&", string.Empty), answer, StringComparison.OrdinalIgnoreCase));
      }

      if (chosen >= 0)
      {
        return chosen;
      }
    }
  }

  public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) =>
    PromptForCredential(caption, message, userName, targetName, PSCredentialTypes.Default, PSCredentialUIOptions.Default);

  public override PSCredential PromptForCredential(
    string caption,
    string message,
    string userName,
    string targetName,
    PSCredentialTypes allowedCredentialTypes,
    PSCredentialUIOptions options)
  {
    WriteHeading(caption, message);
    if (string.IsNullOrEmpty(userName))
    {
      Write("User: ");
      userName = ReadLine();
    }

    Write($"Password for user {userName}: ");
    return new PSCredential(userName, ReadLineAsSecureString());
  }

  private void WriteHeading(string caption, string message)
  {
    if (!string.IsNullOrEmpty(caption))
    {
      WriteLine(caption);
    }

    if (!string.IsNullOrEmpty(message))
    {
      WriteLine(message);
    }
  }

  // The letter after '&' in a label such as "&Yes"; the first letter without one.
  private static string HotKey(ChoiceDescription choice)
  {
    var amp = choice.Label.IndexOf('&');
    return amp >= 0 && amp + 1 < choice.Label.Length
      ? choice.Label[amp + 1].ToString().ToUpperInvariant()
      : choice.Label[..Math.Min(1, choice.Label.Length)].ToUpperInvariant();
  }

  private static SecureString Secure(string text)
  {
    var secure = new SecureString();
    foreach (var character in text)
    {
      secure.AppendChar(character);
    }

    secure.MakeReadOnly();
    return secure;
  }
}

/// <summary>
/// A fixed 120 by 50 window for PowerShell's formatting; reading single keys isn't possible in a terminal that sends
/// whole lines.
/// </summary>
internal sealed class TerminalRawUserInterface(ITerminalConsole console) : PSHostRawUserInterface
{
  private static readonly Size Window = new(120, 50);

  public override ConsoleColor BackgroundColor { get; set; } = ConsoleColor.Black;
  public override ConsoleColor ForegroundColor { get; set; } = ConsoleColor.Gray;
  public override Size BufferSize { get; set; } = new(Window.Width, 9999);
  public override Coordinates CursorPosition { get; set; }
  public override int CursorSize { get; set; } = 25;
  public override bool KeyAvailable => false;
  public override Size MaxPhysicalWindowSize => Window;
  public override Size MaxWindowSize => Window;
  public override Coordinates WindowPosition { get; set; }
  public override Size WindowSize { get; set; } = Window;
  public override string WindowTitle { get; set; } = "StealthDesk Terminal";

  public override void FlushInputBuffer() { }

  public override KeyInfo ReadKey(ReadKeyOptions options) =>
    throw new NotSupportedException("The terminal sends whole lines; reading single keys isn't possible.");

  public override BufferCell[,] GetBufferContents(Rectangle rectangle) =>
    throw new NotSupportedException("The terminal has no screen buffer to read.");

  public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill) { }
  public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) { }
  // Clear-Host (cls) fills the whole screen, told by a rectangle of -1s, with blanks: the terminal empties its screen.
  public override void SetBufferContents(Rectangle rectangle, BufferCell fill)
  {
    if (rectangle is { Left: -1, Top: -1, Right: -1, Bottom: -1 })
    {
      console.Clear();
    }
  }
}
