using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.Relay;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl;
using MessagePack;

namespace StealthDesk.Web.Client.RemoteControl;

/// <summary>
/// The browser's end of a remote control session: it joins the relay with the ticket the server gave, draws what the
/// desktop client sends, and sends back the viewer's choices and input. The stream protocol is the same one the
/// desktop client speaks, reused here, so only the drawing and the input capture are the browser's own.
/// </summary>
public sealed class RemoteDesktopConnection(Uri server, RemoteControlTicket ticket, TimeProvider clock, ILogger<RemoteDesktopConnection> logger) : IRemoteDesktopConnection
{
  private readonly ClientWebSocket _socket = new();
  private RelayStream? _stream;
  private CancellationTokenSource? _running;

  /// <summary>The displays the device offers, and which one is shown; raised at the start and whenever they change.</summary>
  public event Action<DisplayList>? DisplaysChanged;

  /// <summary>A batch of screen regions to draw, in the shown display's pixels.</summary>
  public event Action<ScreenRegions>? RegionsReceived;

  /// <summary>The pointer's shape on the device changed.</summary>
  public event Action<CursorShape>? CursorChanged;

  /// <summary>How the stream is doing, once a second.</summary>
  public event Action<CaptureMetrics>? MetricsReceived;

  /// <summary>Done when the session ends, with why (the relay closing, the device leaving, an error).</summary>
  public event Action<string>? Ended;

  /// <summary>Joins the relay and starts receiving in the background. Throws if the relay refuses the join.</summary>
  public async Task StartAsync(CancellationToken cancellationToken)
  {
    var address = RelayConnection.For(server, ticket.RelaySessionId, RelayRole.Viewer, ticket.ViewerToken);
    // The browser keeps the WebSocket alive on its own; the device's and server's ends send the pings a proxy needs.
    await _socket.ConnectAsync(address, cancellationToken);

    _stream = new RelayStream(_socket, clock, logger);
    _running = new CancellationTokenSource();
    _ = ReceiveAsync(_running.Token);
  }

  /// <summary>Shows another of the device's displays; its whole screen comes next.</summary>
  public Task ChangeDisplayAsync(string displayId) => SendAsync(StreamMessageType.ChangeDisplay, new ChangeDisplay(displayId));

  /// <summary>Asks for the whole screen to be sent again, e.g. after resizing the view.</summary>
  public Task RequestFullFrameAsync() => SendAsync(StreamMessageType.RequestFullFrame, new RequestFullFrame());

  /// <summary>Changes how the screen is encoded from now on.</summary>
  public Task ApplyAsync(CaptureSettings settings) => SendAsync(StreamMessageType.CaptureSettings, settings);

  public Task MovePointerAsync(double x, double y) => SendAsync(StreamMessageType.MovePointer, new MovePointer(x, y));

  public Task MouseButtonAsync(int button, bool isPressed, double x, double y) =>
    SendAsync(StreamMessageType.MouseButton, new MouseButton(button, isPressed, x, y));

  public Task MouseWheelAsync(double x, double y, int deltaY, int deltaX) =>
    SendAsync(StreamMessageType.MouseWheel, new MouseWheel(x, y, deltaY, deltaX));

  public Task KeyAsync(string key, string code, bool isPressed, KeyModifiers modifiers) =>
    SendAsync(StreamMessageType.KeyEvent, new KeyEvent(key, code, isPressed, modifiers));

  public Task TypeTextAsync(string text) => SendAsync(StreamMessageType.TypeText, new TypeText(text));

  public Task ResetKeyboardAsync() => SendAsync(StreamMessageType.ResetKeyboard, new ResetKeyboard());

  public Task CtrlAltDelAsync() => SendAsync(StreamMessageType.CtrlAltDel, new CtrlAltDel());

  public async ValueTask DisposeAsync()
  {
    if (_running is { } running)
    {
      await running.CancelAsync();
      running.Dispose();
    }

    try
    {
      if (_socket.State is WebSocketState.Open)
      {
        using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "The viewer left.", closing.Token);
      }
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
    {
      // Already gone.
    }

    _socket.Dispose();
  }

  private async Task ReceiveAsync(CancellationToken cancellationToken)
  {
    var reason = "The connection closed.";
    try
    {
      reason = await _stream!.RunAsync(Handle, cancellationToken);
    }
    catch (Exception ex)
    {
      reason = ex.Message;
    }
    finally
    {
      Ended?.Invoke(reason);
    }
  }

  private Task Handle(StreamEnvelope message, CancellationToken cancellationToken)
  {
    try
    {
      switch (message.Type)
      {
        case StreamMessageType.DisplayList:
          DisplaysChanged?.Invoke(MessagePackSerializer.Deserialize<DisplayList>(message.Payload));
          break;
        case StreamMessageType.ScreenRegions:
          RegionsReceived?.Invoke(MessagePackSerializer.Deserialize<ScreenRegions>(message.Payload));
          break;
        case StreamMessageType.CursorShape:
          CursorChanged?.Invoke(MessagePackSerializer.Deserialize<CursorShape>(message.Payload));
          break;
        case StreamMessageType.CaptureMetrics:
          MetricsReceived?.Invoke(MessagePackSerializer.Deserialize<CaptureMetrics>(message.Payload));
          break;
        default:
          logger.LogDebug("The viewer ignored a {Type} message.", message.Type);
          break;
      }
    }
    catch (MessagePackSerializationException ex)
    {
      logger.LogDebug(ex, "The viewer couldn't read a {Type} message.", message.Type);
    }

    return Task.CompletedTask;
  }

  private Task SendAsync<T>(StreamMessageType type, T message) =>
    _stream is { } stream && _running is { IsCancellationRequested: false } running
      ? stream.SendAsync(type, message, running.Token)
      : Task.CompletedTask;
}
