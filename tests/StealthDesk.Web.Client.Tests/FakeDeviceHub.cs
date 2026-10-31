using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.Contracts.Terminal;
using StealthDesk.Web.Client.Devices;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The device calls without a server: it records each call and answers as the test sets.</summary>
internal sealed class FakeDeviceHub : IDeviceHub
{
  public event Action<TerminalOutput>? TerminalOutputReceived;

  public event Action? ConnectionLost;

  public List<string> Calls { get; } = [];

  public Guid NextTerminal { get; set; } = Guid.NewGuid();

  public GatewayReply<DeviceActions> Actions { get; set; } = GatewayReply.Accept(new DeviceActions(UseTerminal: true));

  /// <summary>What opening answers; by default the next terminal, after sending a banner and a prompt for it.</summary>
  public Func<Guid, GatewayReply<Guid>>? OpenReply { get; set; }

  /// <summary>Held until the test lets go: an open still waiting for its answer.</summary>
  public TaskCompletionSource? HoldOpen { get; set; }

  public GatewayReply<TerminalInputReceipt>? InputReply { get; set; }

  /// <summary>The next lines answer a question the session asked, rather than being commands.</summary>
  public bool Answering { get; set; }

  public Task<GatewayReply<DeviceActions>> GetDeviceActionsAsync(Guid deviceId)
  {
    Calls.Add($"actions {deviceId}");
    return Task.FromResult(Actions);
  }

  public Task<GatewayReply<Guid>> StartDeviceAccessAsync(Guid deviceId)
  {
    Calls.Add($"visit {deviceId}");
    return Task.FromResult(GatewayReply.Accept(deviceId));
  }

  public Task EndDeviceAccessAsync()
  {
    Calls.Add("end visit");
    return Task.CompletedTask;
  }

  public async Task<GatewayReply<Guid>> OpenTerminalAsync(Guid deviceId)
  {
    Calls.Add($"open {deviceId}");
    var terminal = NextTerminal;
    if (OpenReply is null)
    {
      // As the server does: the banner and prompt arrive before the answer.
      Send(terminal, TerminalOutputKind.Output, "PowerShell 7.6.8 on Windows\nPS C:\\> ");
    }

    if (HoldOpen is { } hold)
    {
      await hold.Task;
    }

    return OpenReply?.Invoke(deviceId) ?? GatewayReply.Accept(terminal);
  }

  public Task<GatewayReply<TerminalInputReceipt>> SendTerminalInputAsync(Guid terminalId, string line)
  {
    Calls.Add($"input {terminalId} {line}");
    return Task.FromResult(InputReply ?? GatewayReply.Accept(new TerminalInputReceipt(terminalId, Answering)));
  }

  /// <summary>What completes, as the device's PowerShell would: the last word up to the caret, by its start.</summary>
  public List<string> Completable { get; set; } = ["Get-ChildItem", "Get-Command", "Get-Content"];

  /// <summary>What completing answers instead, e.g. a refusal.</summary>
  public GatewayReply<TerminalCompletions>? CompletionReply { get; set; }

  private (string Input, int Cursor)? _completing;
  private int _next = -1;

  public Task<GatewayReply<TerminalCompletions>> GetTerminalCompletionsAsync(Guid terminalId, TerminalCompletionRequest request)
  {
    Calls.Add($"complete {request.Input}|{request.Cursor}|{request.Forward}|{request.Page}");
    if (CompletionReply is { } reply)
    {
      return Task.FromResult(reply);
    }

    var start = request.Input.LastIndexOf(' ', Math.Max(0, request.Cursor - 1)) + 1;
    var word = request.Input[start..request.Cursor];
    var matches = Completable.Where(x => x.StartsWith(word, StringComparison.OrdinalIgnoreCase))
      .Select(x => new TerminalCompletionMatch(x, x, TerminalCompletionKind.Command, $"{x} [args]"))
      .ToList();
    TerminalCompletions Answer(IReadOnlyList<TerminalCompletionMatch> some, int total, int page, bool more) =>
      new(start, request.Cursor - start, some, total, page, more);

    if (request.Forward is { } forward)
    {
      if (_completing != (request.Input, request.Cursor))
      {
        _completing = (request.Input, request.Cursor);
        _next = -1;
      }

      if (matches.Count == 0)
      {
        return Task.FromResult(GatewayReply.Accept(Answer([], 0, 0, false)));
      }

      _next = forward ? (_next + 1) % matches.Count : (_next <= 0 ? matches.Count : _next) - 1;
      return Task.FromResult(GatewayReply.Accept(Answer([matches[_next]], 1, 0, false)));
    }

    if (matches.Count > TerminalCompletions.MaxListed)
    {
      return Task.FromResult(GatewayReply.Accept(Answer([], matches.Count, request.Page, false)));
    }

    var size = TerminalCompletionRequest.PageSize;
    return Task.FromResult(GatewayReply.Accept(Answer(
      [.. matches.Skip(request.Page * size).Take(size)], matches.Count, request.Page, (request.Page + 1) * size < matches.Count)));
  }

  public Task<GatewayReply<Guid>> CloseTerminalAsync(Guid terminalId)
  {
    Calls.Add($"close {terminalId}");
    return Task.FromResult(GatewayReply.Accept(terminalId));
  }

  public GatewayReply<IReadOnlyList<RemoteDesktopSession>> DesktopSessions { get; set; } =
    GatewayReply.Accept<IReadOnlyList<RemoteDesktopSession>>([new RemoteDesktopSession(1, "user", IsConsole: true)]);

  public GatewayReply<RemoteControlTicket> RemoteControlTicket { get; set; } =
    GatewayReply.Accept(new RemoteControlTicket(Guid.NewGuid(), "viewer-token"));

  public Task<GatewayReply<IReadOnlyList<RemoteDesktopSession>>> GetDesktopSessionsAsync(Guid deviceId)
  {
    Calls.Add($"sessions {deviceId}");
    return Task.FromResult(DesktopSessions);
  }

  public Task<GatewayReply<RemoteControlTicket>> StartRemoteControlAsync(Guid deviceId, int desktopSessionId)
  {
    Calls.Add($"remote {deviceId} {desktopSessionId}");
    return Task.FromResult(RemoteControlTicket);
  }

  public void Send(Guid terminal, TerminalOutputKind kind, string text) =>
    TerminalOutputReceived?.Invoke(new TerminalOutput(terminal, kind, text));

  public void Drop() => ConnectionLost?.Invoke();
}
