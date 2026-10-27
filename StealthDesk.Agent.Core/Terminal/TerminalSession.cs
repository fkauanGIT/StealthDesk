using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.PowerShell;
using StealthDesk.Contracts.Terminal;
using StealthDesk.Core;

namespace StealthDesk.Agent.Core.Terminal;

/// <summary>How long a terminal session waits for its user.</summary>
public sealed class TerminalOptions
{
  /// <summary>A session with no command running and nothing typed for this long is closed.</summary>
  public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

  /// <summary>
  /// A question (Read-Host, a confirmation) with no answer for this long goes on as if Enter was pressed: an empty
  /// line, or the choice's default.
  /// </summary>
  public TimeSpan AnswerTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// One terminal: a PowerShell runspace that keeps its variables, modules and folder between commands, running one
/// command at a time in the order they were typed. A line typed while a command waits for an answer is that answer.
/// </summary>
internal sealed partial class TerminalSession : ITerminalConsole, IAsyncDisposable
{
  private readonly TerminalOptions _options;
  private readonly TimeProvider _clock;
  private readonly ILogger _logger;
  private readonly Action<TerminalSession> _ended;
  private readonly TerminalOutputBuffer _output;
  private readonly Channel<string> _commands = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
  private readonly Lock _gate = new();
  private readonly ITimer _idle;

  // PowerShell runs one thing at a time: a command, or working out completions.
  private readonly SemaphoreSlim _runspaceInUse = new(1, 1);
  private Runspace? _runspace;
  private Task _running = Task.CompletedTask;
  private PowerShell? _current;
  private TaskCompletionSource<string>? _answer;
  private int? _exitCode;
  private CommandCompletion? _completion;
  private (string Input, int Cursor) _completed;
  private bool _busy;
  private Task? _disposing;

  public TerminalSession(
    Guid id,
    ITerminalOutputSink sink,
    TerminalOptions options,
    TimeProvider clock,
    ILogger logger,
    Action<TerminalSession> ended)
  {
    Id = id;
    _options = options;
    _clock = clock;
    _logger = logger;
    _ended = ended;
    _output = new TerminalOutputBuffer(id, sink, clock, logger);
    _idle = clock.CreateTimer(_ => CloseIfIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
  }

  public Guid Id { get; }

  public bool HasEnded { get; private set; }

  /// <summary>The state of the session's PowerShell, once it was created.</summary>
  internal RunspaceState? PowerShellState => _runspace?.RunspaceStateInfo.State;

  /// <summary>Opens the runspace in <paramref name="startFolder"/> and shows the banner and the first prompt.</summary>
  /// <param name="modulesFolder">PowerShell's built-in modules (<see cref="PowerShellModules"/>).</param>
  public async Task StartAsync(string startFolder, string modulesFolder)
  {
    var state = InitialSessionState.CreateDefault2();
    // Loaded up front from the agent's copy: PowerShell only loads its built-in modules on demand from the folder it
    // runs from, which an agent made of one executable doesn't have.
    state.ImportPSModulesFromPath(modulesFolder);
    if (OperatingSystem.IsWindows())
    {
      // For this process only, as `Set-ExecutionPolicy RemoteSigned -Scope Process` would: local scripts run,
      // downloaded ones need a signature.
      state.ExecutionPolicy = ExecutionPolicy.RemoteSigned;
    }

    var runspace = RunspaceFactory.CreateRunspace(new TerminalHost(this), state);
    runspace.ThreadOptions = PSThreadOptions.ReuseThread;
    runspace.StateChanged += (_, e) =>
    {
      if (e.RunspaceStateInfo.State == RunspaceState.Broken)
      {
        End("PowerShell stopped working; the session is over.");
      }
    };

    // Opening loads PowerShell: about a second, off the caller's thread.
    await Task.Run(runspace.Open);
    lock (_gate)
    {
      _runspace = runspace;
      if (HasEnded)
      {
        // Closed while PowerShell loaded, before disposing could reach it: nothing will use it now.
        runspace.Dispose();
        return;
      }
    }

    runspace.SessionStateProxy.Path.SetLocation(WildcardPattern.Escape(startFolder));

    Write(TerminalOutputKind.Output, $"PowerShell {PSVersionInfo.PSVersion} on {RuntimeInformation.OSDescription}\n");
    WritePrompt();
    _running = Task.Run(RunCommandsAsync);
    WaitForUse();
  }

  /// <summary>A line typed in the terminal: the answer to the question waiting, or the next command.</summary>
  /// <returns>What the line was taken as; null when the session is over.</returns>
  public TerminalInputReceipt? Submit(string line)
  {
    lock (_gate)
    {
      if (HasEnded)
      {
        return null;
      }

      _idle.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
      if (_answer is { } answer)
      {
        _answer = null;
        answer.TrySetResult(line);
        return new TerminalInputReceipt(Id, AnsweredQuestion: true);
      }

      return _commands.Writer.TryWrite(line) ? new TerminalInputReceipt(Id, AnsweredQuestion: false) : null;
    }
  }

  /// <summary>
  /// Completions for what is typed, from this session: its commands, variables and folder. The same input asked again
  /// walks the same matches, so Tab after Tab goes through them. Not while a command runs: PowerShell is taken.
  /// </summary>
  public Outcome<TerminalCompletions> Complete(TerminalCompletionRequest request)
  {
    const string Busy = "Completions aren't available while a command runs.";
    lock (_gate)
    {
      if (HasEnded)
      {
        return Outcome.Failure<TerminalCompletions>("The terminal session has ended.");
      }

      if (_runspace is null)
      {
        return Outcome.Failure<TerminalCompletions>(Busy);
      }
    }

    // Taken for as long as a command runs, questions it asks included.
    if (!_runspaceInUse.Wait(0))
    {
      return Outcome.Failure<TerminalCompletions>(Busy);
    }

    try
    {
      var cursor = Math.Clamp(request.Cursor, 0, request.Input.Length);
      if (_completion is null || _completed != (request.Input, cursor))
      {
        using var shell = PowerShell.Create();
        shell.Runspace = _runspace;
        _completion = CommandCompletion.CompleteInput(request.Input, cursor, new Hashtable(), shell);
        _completed = (request.Input, cursor);
      }

      return Answer(_completion, request);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Completing input failed in terminal {TerminalId}.", Id);
      return Outcome.Failure<TerminalCompletions>("PowerShell couldn't complete that.");
    }
    finally
    {
      _runspaceInUse.Release();
    }
  }

  private static TerminalCompletions Answer(CommandCompletion completion, TerminalCompletionRequest request)
  {
    if (request.Forward is { } forward)
    {
      var next = completion.GetNextResult(forward);
      return new TerminalCompletions(
        completion.ReplacementIndex,
        completion.ReplacementLength,
        next is null ? [] : [Match(next)],
        next is null ? 0 : 1,
        Page: 0,
        HasMorePages: false);
    }

    var total = completion.CompletionMatches.Count;
    var page = Math.Max(0, request.Page);
    if (total > TerminalCompletions.MaxListed)
    {
      return new TerminalCompletions(completion.ReplacementIndex, completion.ReplacementLength, [], total, page, HasMorePages: false);
    }

    return new TerminalCompletions(
      completion.ReplacementIndex,
      completion.ReplacementLength,
      [.. completion.CompletionMatches.Skip(page * TerminalCompletionRequest.PageSize).Take(TerminalCompletionRequest.PageSize).Select(Match)],
      total,
      page,
      HasMorePages: (page + 1) * TerminalCompletionRequest.PageSize < total);
  }

  private static TerminalCompletionMatch Match(CompletionResult result) =>
    new(result.CompletionText, result.ListItemText, (TerminalCompletionKind)result.ResultType, result.ToolTip);

  /// <summary>Ends the session: stops the command running and sends <paramref name="reason"/> as its last output.</summary>
  public void End(string reason)
  {
    lock (_gate)
    {
      if (HasEnded)
      {
        return;
      }

      HasEnded = true;
      _idle.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
      _answer?.TrySetException(new PromptingException("The session ended."));
      _answer = null;
      _commands.Writer.TryComplete();
      StopCurrent();
    }

    _output.End(reason);
    _logger.LogInformation("Terminal {TerminalId} ended: {Reason}", Id, reason);
    _ended(this);
  }

  // Once: ending a session also tells its owner, which lets go of it in turn.
  public ValueTask DisposeAsync()
  {
    lock (_gate)
    {
      _disposing ??= DisposeOnceAsync();
      return new ValueTask(_disposing);
    }
  }

  private async Task DisposeOnceAsync()
  {
    await Task.Yield();
    End("The session was closed.");
    await _running;
    await _idle.DisposeAsync();
    _runspace?.Dispose();
    await _output.DisposeAsync();
  }

  void ITerminalConsole.Write(TerminalOutputKind kind, string text) => Write(kind, text);

  string? ITerminalConsole.ReadLine(bool secret)
  {
    var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    lock (_gate)
    {
      if (HasEnded)
      {
        throw new PromptingException("The session ended.");
      }

      _answer = answer;
    }

    // What the question says must reach the browser before anyone can answer it.
    _output.Flush();
    try
    {
      var line = answer.Task.WaitAsync(_options.AnswerTimeout, _clock).GetAwaiter().GetResult();
      Write(TerminalOutputKind.Output, (secret ? new string('*', line.Length) : line) + "\n");
      return line;
    }
    catch (TimeoutException)
    {
      lock (_gate)
      {
        _answer = null;
      }

      Write(TerminalOutputKind.Output, $"\nNo answer within {_options.AnswerTimeout.TotalMinutes:0} minutes: went on as if Enter was pressed.\n");
      return null;
    }
  }

  void ITerminalConsole.RequestExit(int exitCode) => _exitCode = exitCode;

  void ITerminalConsole.Clear() => _output.Clear();

  // The terminal shows what it receives as it is: PowerShell's color codes would show as stray characters.
  private void Write(TerminalOutputKind kind, string text) => _output.Write(kind, AnsiCodes().Replace(text, string.Empty));

  // Control sequences (colors, cursor moves, window titles) as terminals read them.
  [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
  private static partial Regex AnsiCodes();

  private async Task RunCommandsAsync()
  {
    await foreach (var command in _commands.Reader.ReadAllAsync())
    {
      lock (_gate)
      {
        _busy = true;
      }

      // After the prompt it was typed at, as a console shows it.
      Write(TerminalOutputKind.Output, command + "\n");
      await RunAsync(command);
      if (_exitCode is { } code)
      {
        End(code == 0 ? "The session ended." : $"The session ended with exit code {code}.");
        return;
      }

      lock (_gate)
      {
        if (HasEnded)
        {
          return;
        }

        WritePrompt();
        _busy = false;
      }

      WaitForUse();
    }
  }

  private async Task RunAsync(string command)
  {
    if (string.IsNullOrWhiteSpace(command))
    {
      return;
    }

    await _runspaceInUse.WaitAsync();
    using var shell = PowerShell.Create();
    shell.Runspace = _runspace;
    // As the console runs what is typed: errors go along with results, and Out-Default shows them all through the
    // host, formatted as PowerShell's console would. Write-Output in between takes what programs (whoami, ping)
    // write: a program whose output goes straight to Out-Default writes to the agent's own console instead.
    shell.AddScript(command).AddCommand("Write-Output").AddCommand("Out-Default");
    shell.Commands.Commands[0].MergeMyResults(PipelineResultTypes.Error, PipelineResultTypes.Output);
    lock (_gate)
    {
      _current = shell;
    }

    try
    {
      await shell.InvokeAsync();
    }
    catch (PipelineStoppedException)
    {
      // Stopped because the session ended: its last output says why.
    }
    catch (RuntimeException ex) when (ex.ErrorRecord is not null)
    {
      ShowError(ex.ErrorRecord);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "A command failed in terminal {TerminalId}.", Id);
      Write(TerminalOutputKind.Error, ex.Message + "\n");
    }
    finally
    {
      lock (_gate)
      {
        _current = null;
      }

      _runspaceInUse.Release();
    }
  }

  // A terminating error (a throw, a syntax error) formatted the way PowerShell shows any other error, and sent as one.
  private void ShowError(ErrorRecord error)
  {
    var text = error.ToString();
    try
    {
      using var format = PowerShell.Create();
      format.Runspace = _runspace;
      format.AddCommand("Out-String");
      // A syntax error formats to nothing outside a console; its message says where and what then.
      var formatted = string.Concat(format.Invoke(new[] { error })).Trim();
      text = formatted.Length > 0 ? formatted : error.Exception?.Message ?? text;
    }
    catch (Exception ex)
    {
      _logger.LogDebug(ex, "Couldn't format an error in terminal {TerminalId}.", Id);
    }

    Write(TerminalOutputKind.Error, text + "\n");
  }

  private void WritePrompt()
  {
    var location = _runspace?.SessionStateProxy.Path.CurrentLocation.Path;
    Write(TerminalOutputKind.Output, $"PS {location}> ");
    _output.Flush();
  }

  // Under _gate.
  private void StopCurrent()
  {
    if (_current is { } shell)
    {
      shell.BeginStop(null, null);
    }
  }

  private void WaitForUse()
  {
    lock (_gate)
    {
      if (!HasEnded && !_busy)
      {
        _idle.Change(_options.IdleTimeout, Timeout.InfiniteTimeSpan);
      }
    }
  }

  private void CloseIfIdle()
  {
    lock (_gate)
    {
      if (_busy || _answer is not null)
      {
        return;
      }
    }

    End($"Closed after {_options.IdleTimeout.TotalMinutes:0} minutes without use.");
  }
}
