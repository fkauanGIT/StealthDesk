using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using StealthDesk.Contracts.Terminal;
using StealthDesk.Web.Client.Devices;

namespace StealthDesk.Web.Client.Terminal;

public enum TerminalStatus
{
  /// <summary>No session: never opened, or it couldn't open.</summary>
  Closed,
  Opening,
  Open,

  /// <summary>The session is over; <see cref="DeviceTerminal.Notice"/> says why.</summary>
  Ended,
}

/// <summary>A run of text of one kind on a line of the terminal.</summary>
public sealed record TerminalSpan(TerminalOutputKind Kind, string Text);

/// <summary>A line of the terminal; the last one is still being written.</summary>
public sealed class TerminalLine
{
  private readonly List<TerminalSpan> _spans = [];

  public IReadOnlyList<TerminalSpan> Spans => _spans;

  public string Text => string.Concat(_spans.Select(x => x.Text));

  internal void Add(TerminalOutputKind kind, string text)
  {
    if (_spans.Count > 0 && _spans[^1].Kind == kind)
    {
      _spans[^1] = _spans[^1] with { Text = _spans[^1].Text + text };
      return;
    }

    _spans.Add(new TerminalSpan(kind, text));
  }
}

/// <summary>
/// The terminal of the device being looked at: its session, what it showed and what was typed. Kept while the user
/// moves between the pages of that device, and closed when they leave it, so a session never outlives the device's
/// pages.
/// </summary>
public sealed class DeviceTerminal : IDisposable
{
  /// <summary>Lines kept on screen; older ones scroll away for good.</summary>
  public const int MaxLines = 500;

  /// <summary>Commands kept for Up and Down.</summary>
  public const int MaxHistory = 500;

  private readonly IDeviceHub _hub;
  private readonly NavigationManager _navigation;
  private readonly List<TerminalLine> _lines = [];
  private readonly List<string> _history = [];
  private readonly List<TerminalOutput> _early = [];
  private int _historyIndex;
  private string _draft = string.Empty;

  public DeviceTerminal(IDeviceHub hub, NavigationManager navigation)
  {
    _hub = hub;
    _navigation = navigation;
    _hub.TerminalOutputReceived += OnOutput;
    _hub.ConnectionLost += OnConnectionLost;
    _navigation.LocationChanged += OnLocationChanged;
  }

  public event Action? Changed;

  public Guid? DeviceId { get; private set; }

  public Guid? TerminalId { get; private set; }

  public TerminalStatus Status { get; private set; }

  /// <summary>Why the last thing tried didn't work, or why the session ended; null when there is nothing to say.</summary>
  public string? Notice { get; private set; }

  /// <summary>What is typed and not sent yet.</summary>
  public string Input { get; set; } = string.Empty;

  /// <summary>Shift+Enter adds a line, and Up and Down move between lines instead of walking the history.</summary>
  public bool Multiline { get; set; }

  public bool Sending { get; private set; }

  public IReadOnlyList<TerminalLine> Lines => _lines;

  public IReadOnlyList<string> History => _history;

  /// <summary>
  /// Opens a session on the device. A session already open or opening there is kept; one on another device is closed
  /// first, and what it showed goes with it.
  /// </summary>
  public async Task OpenAsync(Guid deviceId)
  {
    if (DeviceId == deviceId && Status is TerminalStatus.Opening or TerminalStatus.Open)
    {
      return;
    }

    if (DeviceId != deviceId)
    {
      await CloseAsync();
      Clear();
      DeviceId = deviceId;
    }

    Status = TerminalStatus.Opening;
    TerminalId = null;
    Notice = null;
    _early.Clear();
    Raise();

    var opened = await _hub.OpenTerminalAsync(deviceId);
    if (DeviceId != deviceId || Status != TerminalStatus.Opening)
    {
      // Left, or the connection dropped, while it opened: nobody will use it.
      if (opened.Accepted)
      {
        await _hub.CloseTerminalAsync(opened.Value);
      }

      return;
    }

    if (!opened.Accepted)
    {
      Status = TerminalStatus.Closed;
      Notice = opened.Error;
      Raise();
      return;
    }

    TerminalId = opened.Value;
    Status = TerminalStatus.Open;
    // The banner and first prompt come before the answer that says which session they belong to.
    foreach (var output in _early.Where(x => x.TerminalId == opened.Value))
    {
      Apply(output);
    }

    _early.Clear();
    Raise();
  }

  /// <summary>
  /// Sends what is typed: a command, or the answer to a question the session asked. An empty line is sent too, as
  /// Enter in a console. Only commands go in the history, as in a console: an answer may be a password. Refused, it
  /// stays typed and the reason shows.
  /// </summary>
  public async Task SubmitAsync()
  {
    if (Status != TerminalStatus.Open || TerminalId is not { } terminal || Sending)
    {
      return;
    }

    var line = Input;
    Sending = true;
    Raise();
    var sent = await _hub.SendTerminalInputAsync(terminal, line);
    Sending = false;
    if (sent.Accepted)
    {
      Remember(sent.Value is { AnsweredQuestion: true } ? string.Empty : line);
      Input = string.Empty;
      Notice = null;
    }
    else
    {
      Notice = sent.Error;
    }

    Raise();
  }

  /// <summary>The command before the one shown; the first step back keeps what was being typed.</summary>
  public void Previous()
  {
    if (_historyIndex == 0)
    {
      return;
    }

    if (_historyIndex == _history.Count)
    {
      _draft = Input;
    }

    Input = _history[--_historyIndex];
    Raise();
  }

  /// <summary>The command after the one shown; past the last one, what was being typed.</summary>
  public void Next()
  {
    if (_historyIndex >= _history.Count)
    {
      return;
    }

    _historyIndex++;
    Input = _historyIndex == _history.Count ? _draft : _history[_historyIndex];
    Raise();
  }

  /// <summary>Clears what is typed; false when there was nothing to clear.</summary>
  public bool ClearInput()
  {
    var had = Input.Length > 0;
    Input = string.Empty;
    _draft = string.Empty;
    _historyIndex = _history.Count;
    Raise();
    return had;
  }

  /// <summary>Ends the session on the device; what it showed stays until the user leaves the device.</summary>
  public async Task CloseAsync()
  {
    var terminal = TerminalId;
    var wasOpen = Status == TerminalStatus.Open;
    TerminalId = null;
    if (Status is TerminalStatus.Open or TerminalStatus.Opening)
    {
      Status = TerminalStatus.Closed;
      Raise();
    }

    if (wasOpen && terminal is { } id)
    {
      await _hub.CloseTerminalAsync(id);
    }
  }

  public void Dispose()
  {
    _hub.TerminalOutputReceived -= OnOutput;
    _hub.ConnectionLost -= OnConnectionLost;
    _navigation.LocationChanged -= OnLocationChanged;
  }

  private void Remember(string line)
  {
    if (!string.IsNullOrWhiteSpace(line) && (_history.Count == 0 || _history[^1] != line))
    {
      _history.Add(line);
      if (_history.Count > MaxHistory)
      {
        _history.RemoveAt(0);
      }
    }

    _draft = string.Empty;
    _historyIndex = _history.Count;
  }

  private void OnOutput(TerminalOutput output)
  {
    if (Status == TerminalStatus.Opening && TerminalId is null)
    {
      _early.Add(output);
      return;
    }

    if (output.TerminalId != TerminalId || Status != TerminalStatus.Open)
    {
      return;
    }

    Apply(output);
    Raise();
  }

  private void Apply(TerminalOutput output)
  {
    if (output.Kind == TerminalOutputKind.Ended)
    {
      Status = TerminalStatus.Ended;
      TerminalId = null;
      Notice = output.Text;
      return;
    }

    Write(output.Kind, output.Text.Replace("\r", string.Empty, StringComparison.Ordinal));
  }

  private void Write(TerminalOutputKind kind, string text)
  {
    if (_lines.Count == 0)
    {
      _lines.Add(new TerminalLine());
    }

    var pieces = text.Split('\n');
    for (var i = 0; i < pieces.Length; i++)
    {
      if (i > 0)
      {
        _lines.Add(new TerminalLine());
      }

      if (pieces[i].Length > 0)
      {
        _lines[^1].Add(kind, pieces[i]);
      }
    }

    if (_lines.Count > MaxLines)
    {
      _lines.RemoveRange(0, _lines.Count - MaxLines);
    }
  }

  // The server closes every terminal of a connection that drops, so this one is over.
  private void OnConnectionLost()
  {
    if (Status is TerminalStatus.Open or TerminalStatus.Opening)
    {
      Status = TerminalStatus.Ended;
      TerminalId = null;
      Notice = "The connection to the server dropped, which closed the session.";
      Raise();
    }
  }

  private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
  {
    if (DeviceId is not { } device || IsPageOf(device, e.Location))
    {
      return;
    }

    _ = LeaveAsync();
  }

  private async Task LeaveAsync()
  {
    await CloseAsync();
    Clear();
    Raise();
  }

  private bool IsPageOf(Guid device, string location)
  {
    var path = _navigation.ToBaseRelativePath(location).Split('?', '#')[0];
    var prefix = $"devices/{device}";
    return path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
  }

  private void Clear()
  {
    DeviceId = null;
    TerminalId = null;
    Status = TerminalStatus.Closed;
    Notice = null;
    Input = string.Empty;
    Multiline = false;
    _draft = string.Empty;
    _lines.Clear();
    _history.Clear();
    _early.Clear();
    _historyIndex = 0;
  }

  private void Raise() => Changed?.Invoke();
}
