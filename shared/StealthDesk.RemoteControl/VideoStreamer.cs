using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.RemoteControl;

namespace StealthDesk.RemoteControl;

/// <summary>
/// Streams one display to the viewer as a continuous video: captures it at a steady rate and feeds that to an
/// encoder, forwarding whatever packets it produces. Idle while the viewer hasn't chosen the video mode, so it costs
/// nothing alongside <see cref="ScreenStreamer"/>, which streams the same display as still regions by default. Which
/// display is shown follows <paramref name="shown"/>, the same source <see cref="ScreenStreamer"/> exposes, so the
/// two never disagree.
/// </summary>
public sealed class VideoStreamer(IScreenCapture capture, IVideoEncoder encoder, RelayStream stream, TimeProvider clock, Func<DisplayInfo?> shown, Func<bool> showCursor, CaptureModeSwitch mode, ILogger logger)
{
  /// <summary>How often a frame is captured and fed to the encoder, while active.</summary>
  public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000 / 30.0);

  private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(100);

  private readonly Lock _gate = new();
  private string? _lastShownId;
  private bool _restart;

  /// <summary>Restarts the stream on a fresh key frame, e.g. because the viewer asked for one.</summary>
  public void RequestKeyFrame()
  {
    lock (_gate)
    {
      _restart = true;
    }
  }

  public async Task RunAsync(CancellationToken cancellationToken)
  {
    var forwarding = ForwardPacketsAsync(cancellationToken);
    try
    {
      while (!cancellationToken.IsCancellationRequested)
      {
        if (mode.Mode != CaptureMode.Video || shown() is not { } display)
        {
          await Task.Delay(IdleDelay, clock, cancellationToken);
          continue;
        }

        if (TakeRestart(display.Id))
        {
          encoder.Start(display.Width, display.Height);
        }

        try
        {
          var captured = capture.Capture(display, fullFrame: true, showCursor());
          if (captured.Frame is { } frame)
          {
            encoder.EncodeFrame(frame);
          }
        }
        catch (Exception ex)
        {
          logger.LogDebug(ex, "Capturing a frame for the video stream failed.");
        }

        await Task.Delay(FrameInterval, clock, cancellationToken);
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      // The session ended.
    }
    finally
    {
      await forwarding;
    }
  }

  // True once per restart needed: the display changed since the last capture, or one was asked for explicitly.
  private bool TakeRestart(string shownId)
  {
    lock (_gate)
    {
      var restart = _restart || _lastShownId != shownId;
      _restart = false;
      _lastShownId = shownId;
      return restart;
    }
  }

  private async Task ForwardPacketsAsync(CancellationToken cancellationToken)
  {
    try
    {
      await foreach (var packet in encoder.Packets(cancellationToken))
      {
        await stream.SendAsync(StreamMessageType.VideoPacket, packet, cancellationToken);
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      // The session ended.
    }
    catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or ObjectDisposedException)
    {
      // The stream ended: whoever runs it says why.
    }
  }
}
