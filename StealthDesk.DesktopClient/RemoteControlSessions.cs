using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.DesktopClients;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl;
using StealthDesk.RemoteControl.Input;
using MessagePack;

namespace StealthDesk.DesktopClient;

/// <summary>Opens the device's end of a relay session.</summary>
internal delegate Task<WebSocket> RelayConnector(Uri address, CancellationToken cancellationToken);

internal static class RelaySocket
{
  /// <summary>Pinged as often as the server pings its end, so a proxy doesn't cut a session whose screen is still.</summary>
  public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

  public static async Task<WebSocket> ConnectAsync(Uri address, CancellationToken cancellationToken)
  {
    var socket = new ClientWebSocket();
    socket.Options.KeepAliveInterval = KeepAlive;
    try
    {
      await socket.ConnectAsync(address, cancellationToken);
      return socket;
    }
    catch
    {
      socket.Dispose();
      throw;
    }
  }
}

/// <summary>Tells the user of this session something, without waiting for them.</summary>
internal interface IUserNotice
{
  void Show(string message);
}

/// <summary>One remote control session of this client, from the agent's request until it leaves the relay.</summary>
internal sealed class RemoteControlSession(DesktopRemoteControlStart request)
{
  public Guid Id => request.RelaySessionId;

  public string ViewerName => request.ViewerName;

  /// <summary>
  /// What the viewer may do besides seeing the desktop: input that needs more than they were given is dropped here,
  /// whatever the browser sends.
  /// </summary>
  public ViewerPermissions Permissions => request.Permissions;

  private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>Done once the session ended, whether or not it ever joined the relay.</summary>
  public Task Ended => _ended.Task;

  public void Finish() => _ended.TrySetResult();
}

/// <summary>
/// The remote control sessions of this desktop client. Each joins its relay session as the device's end, in the
/// background, and lasts until the viewer leaves, the relay ends it, or the client closes. The user is told when one
/// starts and ends unless the tenant turned that off. While it runs, the screen is streamed to the viewer.
/// </summary>
internal sealed class RemoteControlSessions(
  RelayConnector connect,
  Func<IScreenCapture> captures,
  RegionEncoder encode,
  Func<IVideoEncoder> videoEncoders,
  IInputInjector injector,
  IClipboard clipboard,
  IInputDesktop inputDesktop,
  IPrivacyScreen privacyScreen,
  Func<ISessionNotifications> sessionNotifications,
  IUserNotice notice,
  TimeProvider clock,
  IHostApplicationLifetime lifetime,
  ILogger<RemoteControlSessions> logger)
{
  /// <summary>How long joining the relay may take; it answers once the viewer joined too, or gives up first.</summary>
  public static readonly TimeSpan JoinTimeLimit = TimeSpan.FromSeconds(30);

  private readonly ConcurrentDictionary<Guid, RemoteControlSession> _sessions = new();

  public IReadOnlyList<RemoteControlSession> Running => [.. _sessions.Values];

  /// <summary>Starts joining the relay; null once started, otherwise why it can't.</summary>
  public string? Start(DesktopRemoteControlStart request)
  {
    if (request.RelayAddress is not { IsAbsoluteUri: true, Scheme: "ws" or "wss" })
    {
      return "The relay address isn't a WebSocket address.";
    }

    if (lifetime.ApplicationStopping.IsCancellationRequested)
    {
      return "The desktop client is closing.";
    }

    var session = new RemoteControlSession(request);
    if (!_sessions.TryAdd(session.Id, session))
    {
      return "That session is already running.";
    }

    _ = Task.Run(() => RunAsync(session, request.RelayAddress, request.NotifyUser));
    return null;
  }

  private async Task RunAsync(RemoteControlSession session, Uri address, bool notifyUser)
  {
    var joined = false;
    try
    {
      WebSocket socket;
      using (var joining = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping))
      {
        joining.CancelAfter(JoinTimeLimit);
        socket = await connect(address, joining.Token);
      }

      using (socket)
      {
        joined = true;
        logger.LogInformation("Remote control session {SessionId} for {Viewer} started.", session.Id, session.ViewerName);
        if (notifyUser)
        {
          notice.Show($"{session.ViewerName} started remote control of this computer.");
        }

        var stream = new RelayStream(socket, clock, logger);
        using var capture = captures();
        var captureMode = new CaptureModeSwitch();
        var streamer = new ScreenStreamer(capture, encode, stream, clock, logger, captureMode);
        var input = new RemoteInput(injector, () => streamer.Shown, session.Permissions, logger);
        using var videoEncoder = videoEncoders();
        var video = new VideoStreamer(capture, videoEncoder, stream, clock, () => streamer.Shown, () => streamer.Settings.ShowCursor, captureMode, logger);
        using var streaming = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        var sending = streamer.RunAsync(streaming.Token);
        var streamingVideo = video.RunAsync(streaming.Token);
        var guarding = MonitorDesktopAsync(session, stream, streaming.Token);
        using var notifications = sessionNotifications();
        var watching = WatchSessionAsync(session, notifications, stream, streaming.Token);
        var ended = await stream.RunAsync((message, ct) => HandleAsync(session, streamer, video, captureMode, input, stream, message, ct), lifetime.ApplicationStopping);
        await streaming.CancelAsync();
        await sending;
        await streamingVideo;
        await guarding;
        await watching;
        logger.LogDebug("The relay stream of session {SessionId} ended: {Reason}", session.Id, ended);
      }
    }
    // Never the address in the log: it carries the session's token.
    catch (Exception ex) when (!joined)
    {
      logger.LogWarning("Remote control session {SessionId} couldn't join the relay: {Error}", session.Id, ex.Message);
    }
    catch (Exception ex)
    {
      logger.LogInformation("Remote control session {SessionId} lost the relay: {Error}", session.Id, ex.Message);
    }
    finally
    {
      // However the session ended, the device's own mouse and keyboard must work again.
      try
      {
        injector.SetBlockInput(false);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Couldn't unblock input after session {SessionId} ended.", session.Id);
      }

      try
      {
        privacyScreen.SetActive(false);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Couldn't lift the privacy screen after session {SessionId} ended.", session.Id);
      }

      _sessions.TryRemove(new KeyValuePair<Guid, RemoteControlSession>(session.Id, session));
      if (joined)
      {
        logger.LogInformation("Remote control session {SessionId} for {Viewer} ended.", session.Id, session.ViewerName);
        if (notifyUser)
        {
          notice.Show($"{session.ViewerName}'s remote control of this computer ended.");
        }
      }

      session.Finish();
    }
  }

  /// <summary>How often the input desktop is checked, to follow a secure screen or tell the viewer about it.</summary>
  private static readonly TimeSpan DesktopCheck = TimeSpan.FromMilliseconds(500);

  // Follows the input desktop onto a secure screen for a viewer allowed to use it, and tells a viewer who isn't that
  // one is up (and when it goes away).
  private async Task MonitorDesktopAsync(RemoteControlSession session, RelayStream stream, CancellationToken cancellationToken)
  {
    var tracker = new SecureDesktopTracker();
    try
    {
      while (!cancellationToken.IsCancellationRequested)
      {
        var decision = tracker.Poll(inputDesktop.Name(), session.Permissions.UseSecureScreens);
        if (decision.Follow)
        {
          inputDesktop.SwitchToInput();
        }

        if (decision.Notify is { } notify)
        {
          await stream.SendAsync(StreamMessageType.SecureDesktop, notify, cancellationToken);
        }

        await Task.Delay(DesktopCheck, clock, cancellationToken);
      }
    }
    catch (OperationCanceledException)
    {
      // The session ended.
    }
    catch (Exception ex)
    {
      logger.LogDebug(ex, "Watching the input desktop of session {SessionId} stopped.", session.Id);
    }
  }

  // Tells the viewer about a lock, unlock, logoff or user switch of the device's own Windows session.
  private async Task WatchSessionAsync(RemoteControlSession session, ISessionNotifications notifications, RelayStream stream, CancellationToken cancellationToken)
  {
    try
    {
      await foreach (var kind in notifications.Changes.WithCancellation(cancellationToken))
      {
        await stream.SendAsync(StreamMessageType.SessionChanged, new SessionChanged(kind), cancellationToken);
      }
    }
    catch (OperationCanceledException)
    {
      // The session ended.
    }
    catch (Exception ex)
    {
      logger.LogDebug(ex, "Watching session changes of session {SessionId} stopped.", session.Id);
    }
  }

  private async Task HandleAsync(RemoteControlSession session, ScreenStreamer streamer, VideoStreamer video, CaptureModeSwitch captureMode, RemoteInput input, RelayStream stream, StreamEnvelope message, CancellationToken cancellationToken)
  {
    try
    {
      switch (message.Type)
      {
        case StreamMessageType.ChangeDisplay:
          var change = MessagePackSerializer.Deserialize<ChangeDisplay>(message.Payload);
          if (!streamer.ShowDisplay(change.Id))
          {
            logger.LogDebug("Session {SessionId} was asked for display {Display}, which isn't there.", session.Id, change.Id);
          }

          break;
        case StreamMessageType.RequestFullFrame:
          streamer.RequestFullFrame();
          video.RequestKeyFrame();
          break;
        case StreamMessageType.SetCaptureMode:
          var requestedMode = MessagePackSerializer.Deserialize<SetCaptureMode>(message.Payload);
          captureMode.Mode = requestedMode.Mode;
          if (requestedMode.Mode == CaptureMode.Video)
          {
            video.RequestKeyFrame();
          }
          else
          {
            streamer.RequestFullFrame();
          }

          break;
        case StreamMessageType.CaptureSettings:
          streamer.Apply(MessagePackSerializer.Deserialize<CaptureSettings>(message.Payload));
          break;
        case StreamMessageType.MovePointer:
          input.MovePointer(MessagePackSerializer.Deserialize<MovePointer>(message.Payload));
          break;
        case StreamMessageType.MouseButton:
          input.MouseButton(MessagePackSerializer.Deserialize<MouseButton>(message.Payload));
          break;
        case StreamMessageType.MouseWheel:
          input.MouseWheel(MessagePackSerializer.Deserialize<MouseWheel>(message.Payload));
          break;
        case StreamMessageType.KeyEvent:
          input.Key(MessagePackSerializer.Deserialize<KeyEvent>(message.Payload));
          break;
        case StreamMessageType.TypeText:
          input.TypeText(MessagePackSerializer.Deserialize<TypeText>(message.Payload));
          break;
        case StreamMessageType.ResetKeyboard:
          input.ResetKeyboard();
          break;
        case StreamMessageType.CtrlAltDel:
          input.CtrlAltDel();
          break;
        case StreamMessageType.BlockInput:
          var requested = MessagePackSerializer.Deserialize<BlockInput>(message.Payload);
          if (input.SetBlockInput(requested.Active) is { } active)
          {
            await stream.SendAsync(StreamMessageType.BlockInput, new BlockInput(active), cancellationToken);
          }

          break;
        case StreamMessageType.PrivacyScreen:
          await SetPrivacyScreenAsync(session, stream, MessagePackSerializer.Deserialize<PrivacyScreen>(message.Payload), cancellationToken);
          break;
        case StreamMessageType.ClipboardText:
          await WriteClipboardAsync(session, MessagePackSerializer.Deserialize<ClipboardText>(message.Payload));
          break;
        case StreamMessageType.RequestClipboard:
          await SendClipboardAsync(session, stream, cancellationToken);
          break;
        default:
          logger.LogDebug("Session {SessionId} ignored a {Type} message.", session.Id, message.Type);
          break;
      }
    }
    catch (MessagePackSerializationException ex)
    {
      logger.LogDebug(ex, "Session {SessionId} couldn't read a {Type} message.", session.Id, message.Type);
    }
  }

  // The viewer may show or hide the privacy screen only with the interact permission, under which the button appears.
  private async Task SetPrivacyScreenAsync(RemoteControlSession session, RelayStream stream, PrivacyScreen requested, CancellationToken cancellationToken)
  {
    if (!session.Permissions.Interact)
    {
      logger.LogDebug("A viewer without the interact permission tried to use the privacy screen of session {SessionId}.", session.Id);
      return;
    }

    var active = privacyScreen.SetActive(requested.Active);
    await stream.SendAsync(StreamMessageType.PrivacyScreen, new PrivacyScreen(active), cancellationToken);
  }

  // The viewer may set the device's clipboard only with the write permission.
  private async Task WriteClipboardAsync(RemoteControlSession session, ClipboardText message)
  {
    if (!session.Permissions.WriteClipboard)
    {
      logger.LogDebug("A viewer without the clipboard-write permission tried to set session {SessionId}'s clipboard.", session.Id);
      return;
    }

    await clipboard.SetTextAsync(Clipboard.Limit(message.Text));
  }

  // The device's clipboard goes back only with the read permission.
  private async Task SendClipboardAsync(RemoteControlSession session, RelayStream stream, CancellationToken cancellationToken)
  {
    if (!session.Permissions.ReadClipboard)
    {
      logger.LogDebug("A viewer without the clipboard-read permission asked for session {SessionId}'s clipboard.", session.Id);
      return;
    }

    var text = await clipboard.GetTextAsync() ?? string.Empty;
    await stream.SendAsync(StreamMessageType.ClipboardText, new ClipboardText(Clipboard.Limit(text)), cancellationToken);
  }
}
