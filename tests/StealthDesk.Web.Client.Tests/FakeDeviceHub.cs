using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;
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

  public Task<GatewayReply<Guid>> CloseTerminalAsync(Guid terminalId)
  {
    Calls.Add($"close {terminalId}");
    return Task.FromResult(GatewayReply.Accept(terminalId));
  }

  public void Send(Guid terminal, TerminalOutputKind kind, string text) =>
    TerminalOutputReceived?.Invoke(new TerminalOutput(terminal, kind, text));

  public void Drop() => ConnectionLost?.Invoke();
}
