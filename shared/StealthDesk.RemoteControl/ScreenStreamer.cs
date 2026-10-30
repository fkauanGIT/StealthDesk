using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.RemoteControl;

namespace StealthDesk.RemoteControl;

/// <summary>One capture of a display.</summary>
/// <param name="Frame">
/// The screen; none when nothing changed since the last capture, or it failed. It stays as it is until the capture
/// after next, so it can be compared with the next one.
/// </param>
/// <param name="Changed">What changed, when the capture knows it; none to have the frames compared.</param>
/// <param name="Mode">How it was captured, e.g. "GDI".</param>
/// <param name="Error">Why it failed; none when it didn't.</param>
public sealed record CaptureResult(Frame? Frame, IReadOnlyList<Region>? Changed, string Mode, string? Error = null)
{
  public static CaptureResult NoChange(string mode) => new(null, null, mode);

  public static CaptureResult Failed(string mode, string error) => new(null, null, mode, error);
}

/// <summary>A platform's way of seeing its screens.</summary>
public interface IScreenCapture : IDisposable
{
  IReadOnlyList<DisplayInfo> Displays();

  /// <param name="fullFrame">The whole screen is needed: a capture that only reports changes must capture anyway.</param>
  CaptureResult Capture(DisplayInfo display, bool fullFrame);
}

/// <summary>Regions of a frame as images.</summary>
public delegate ScreenRegions RegionEncoder(Frame frame, IReadOnlyList<Region> regions, ImageEncoding encoding, int quality);

/// <summary>
/// Streams one display to the viewer: captures it, finds what changed and sends that, as fast as the stream takes it.
/// The stream holds a send while too much is unacknowledged, so each capture is as recent as the connection allows. The
/// whole screen goes first, after the display shown changes, and when the viewer asks for it.
/// </summary>
public sealed class ScreenStreamer(IScreenCapture capture, RegionEncoder encode, RelayStream stream, TimeProvider clock, ILogger logger)
{
  private static readonly TimeSpan NoChangeDelay = TimeSpan.FromMilliseconds(10);
  private static readonly TimeSpan FailureDelay = TimeSpan.FromMilliseconds(100);

  private readonly Lock _gate = new();
  private DisplayInfo? _shown;
  private IReadOnlyList<DisplayInfo> _displays = [];
  private bool _fullFrame = true;
  private bool _displaysChanged;

  /// <summary>How often the displays are listed again, to tell the viewer when they change.</summary>
  public TimeSpan DisplayCheckInterval { get; init; } = TimeSpan.FromSeconds(2);

  public ImageEncoding Encoding { get; set; } = ImageEncoding.Jpeg;

  public int Quality { get; set; } = 80;

  public void RequestFullFrame()
  {
    lock (_gate)
    {
      _fullFrame = true;
    }
  }

  /// <summary>Shows the display with this id instead; false when there is none.</summary>
  public bool ShowDisplay(string id)
  {
    lock (_gate)
    {
      if (_displays.FirstOrDefault(x => x.Id == id) is not { } display)
      {
        return false;
      }

      _shown = display;
      _fullFrame = true;
      _displaysChanged = true;
      return true;
    }
  }

  public async Task RunAsync(CancellationToken cancellationToken)
  {
    Frame? previous = null;
    var nextDisplayCheck = clock.GetUtcNow();
    while (!cancellationToken.IsCancellationRequested)
    {
      try
      {
        if (clock.GetUtcNow() >= nextDisplayCheck)
        {
          ReloadDisplays();
          nextDisplayCheck = clock.GetUtcNow() + DisplayCheckInterval;
        }

        if (TakeDisplayList() is { } list)
        {
          await stream.SendAsync(StreamMessageType.DisplayList, list, cancellationToken);
        }

        var (shown, fullFrame) = TakeShown();
        if (shown is null)
        {
          await Task.Delay(FailureDelay, clock, cancellationToken);
          continue;
        }

        var captured = capture.Capture(shown, fullFrame);
        if (captured.Error is not null)
        {
          logger.LogDebug("Capturing {Display} failed ({Mode}): {Error}", shown.Id, captured.Mode, captured.Error);
          // The whole screen next time: what failed may have been half drawn.
          RequestFullFrame();
          await Task.Delay(FailureDelay, clock, cancellationToken);
          continue;
        }

        if (captured.Frame is not { } frame)
        {
          if (fullFrame)
          {
            RequestFullFrame();
          }

          await Task.Delay(NoChangeDelay, clock, cancellationToken);
          continue;
        }

        IReadOnlyList<Region> regions = fullFrame
          ? [frame.Bounds]
          : captured.Changed is { } changed
            ? FrameDiff.AlongSections(frame.Width, frame.Height, changed)
            : FrameDiff.Changes(frame, previous);
        previous = frame;
        if (regions.Count == 0)
        {
          await Task.Delay(NoChangeDelay, clock, cancellationToken);
          continue;
        }

        await stream.SendAsync(StreamMessageType.ScreenRegions, encode(frame, regions, Encoding, Quality), cancellationToken);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        return;
      }
      catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or ObjectDisposedException)
      {
        // The stream ended: whoever runs it says why.
        return;
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Streaming the screen failed; trying again.");
        RequestFullFrame();
        await Task.Delay(FailureDelay, clock, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
      }
    }
  }

  private void ReloadDisplays()
  {
    var displays = capture.Displays();
    lock (_gate)
    {
      if (!displays.SequenceEqual(_displays))
      {
        _displays = displays;
        _displaysChanged = true;
      }

      // The display shown, as it is now; the primary one when it is gone.
      var shown = displays.FirstOrDefault(x => x.Id == _shown?.Id) ?? displays.FirstOrDefault(x => x.IsPrimary) ?? displays.FirstOrDefault();
      if (shown != _shown)
      {
        _shown = shown;
        _fullFrame = true;
      }
    }
  }

  private DisplayList? TakeDisplayList()
  {
    lock (_gate)
    {
      if (!_displaysChanged || _shown is null)
      {
        return null;
      }

      _displaysChanged = false;
      return new DisplayList(_displays, _shown.Id);
    }
  }

  private (DisplayInfo? Shown, bool FullFrame) TakeShown()
  {
    lock (_gate)
    {
      var taken = (_shown, _fullFrame);
      _fullFrame = false;
      return taken;
    }
  }
}
