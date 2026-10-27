using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StealthDesk.Agent.Core.Settings;
using StealthDesk.Agent.Core.Terminal;
using StealthDesk.Contracts.Terminal;

namespace StealthDesk.Agent.Tests.Fakes;

/// <summary>Terminal sessions running real PowerShell, with everything they send kept in order.</summary>
public sealed class TerminalHarness : ITerminalOutputSink, IAsyncDisposable
{
  private readonly List<TerminalOutput> _received = [];
  private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"sd-terminal-{Guid.NewGuid():N}");

  public TerminalHarness(TimeProvider? clock = null, TerminalOptions? options = null)
  {
    Sessions = new TerminalSessions(
      this,
      new AgentPaths("tests", isDebugBuild: true, _dataRoot),
      Options.Create(options ?? new TerminalOptions()),
      clock ?? TimeProvider.System,
      NullLoggerFactory.Instance);
  }

  public TerminalSessions Sessions { get; }

  public Guid Terminal { get; } = Guid.NewGuid();

  public IReadOnlyList<TerminalOutput> Received
  {
    get
    {
      lock (_received)
      {
        return [.. _received];
      }
    }
  }

  /// <summary>Everything shown so far, of every kind, joined.</summary>
  public string Screen => string.Concat(Received.Where(x => x.Kind != TerminalOutputKind.Ended).Select(x => x.Text));

  public string Errors => string.Concat(Received.Where(x => x.Kind == TerminalOutputKind.Error).Select(x => x.Text));

  public TerminalOutput? Ended => Received.FirstOrDefault(x => x.Kind == TerminalOutputKind.Ended);

  public Task SendAsync(TerminalOutput output, CancellationToken cancellationToken)
  {
    lock (_received)
    {
      _received.Add(output);
    }

    return Task.CompletedTask;
  }

  /// <summary>Opens the session and waits for its first prompt.</summary>
  public async Task OpenAsync()
  {
    var opened = await Sessions.OpenAsync(Terminal, TestContext.Current.CancellationToken);
    Assert.True(opened.Succeeded, opened.Error);
    await UntilAsync(() => Screen.EndsWith("> ", StringComparison.Ordinal));
  }

  /// <summary>
  /// Types a line and waits until the next prompt; answers what the command showed in between, after the line
  /// itself, which the session shows first.
  /// </summary>
  public async Task<string> RunAsync(string line)
  {
    var before = Screen.Length;
    Send(line);
    await UntilAsync(() => Screen.Length > before && Screen.EndsWith("> ", StringComparison.Ordinal) && Screen[before..].Contains("PS ", StringComparison.Ordinal));
    var shown = Screen[before..];
    Assert.StartsWith(line + "\n", shown);
    return shown[(line.Length + 1)..shown.LastIndexOf("PS ", StringComparison.Ordinal)];
  }

  /// <summary>Answers the question waiting and waits until the next prompt; answers everything shown in between.</summary>
  public async Task<string> AnswerAsync(string answer)
  {
    var before = Screen.Length;
    Send(answer);
    await UntilAsync(() => Screen.Length > before && Screen.EndsWith("> ", StringComparison.Ordinal) && Screen[before..].Contains("PS ", StringComparison.Ordinal));
    var shown = Screen[before..];
    return shown[..shown.LastIndexOf("PS ", StringComparison.Ordinal)];
  }

  public void Send(string line) => Assert.True(Sessions.Send(Terminal, line).Succeeded, "The session has ended.");

  /// <summary>Waits until the terminal shows <paramref name="text"/> after what it showed before.</summary>
  public Task ShowsAsync(string text, int from = 0) => UntilAsync(() => Screen.IndexOf(text, from, StringComparison.Ordinal) >= 0, $"the terminal to show \"{text}\"");

  public async Task UntilAsync(Func<bool> condition, string? what = null, TimeSpan? within = null)
  {
    var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(30));
    while (!condition())
    {
      if (DateTime.UtcNow > deadline)
      {
        Assert.Fail($"Waited for {what ?? "the terminal"}; it shows:\n{Screen}\nEnded: {Ended?.Text ?? "no"}");
      }

      await Task.Delay(20, TestContext.Current.CancellationToken);
    }
  }

  public async ValueTask DisposeAsync()
  {
    await Sessions.DisposeAsync();
    try
    {
      Directory.Delete(_dataRoot, recursive: true);
    }
    catch (IOException)
    {
    }
  }
}
