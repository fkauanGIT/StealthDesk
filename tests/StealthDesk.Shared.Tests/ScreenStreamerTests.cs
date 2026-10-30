using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl;

namespace StealthDesk.Shared.Tests;

/// <summary>
/// Streaming a display to the viewer, from a screen the test paints: the displays and the whole screen first, then
/// only what changed, the whole screen again when asked or when the display changes, and nothing captured while the
/// connection is full.
/// </summary>
public sealed class ScreenStreamerTests : IAsyncDisposable
{
  private static readonly DisplayInfo Primary = new("one", "Display 1", 0, 0, 800, 600, IsPrimary: true);
  // The same size as the primary one: switching must send it whole because it was asked to, not because its size differs.
  private static readonly DisplayInfo Second = new("two", "Display 2", 800, 0, 800, 600, IsPrimary: false);

  private readonly PaintedScreen _screen = new([Primary, Second]);
  private readonly ConcurrentQueue<StreamEnvelope> _received = new();
  private readonly CancellationTokenSource _stop = new();
  private WebSocketPair _pair = null!;
  private ScreenStreamer _streamer = null!;
  private RelayStream _viewer = null!;
  private Task _streaming = Task.CompletedTask;

  private static CancellationToken Token => TestContext.Current.CancellationToken;

  [Fact]
  public async Task First_TheDisplays_ThenThePrimaryScreen_Whole()
  {
    await StartAsync();

    var frame = await NextFrameAsync();

    var list = Read<DisplayList>(_received.First());
    Assert.Equal([Primary, Second], list.Displays);
    Assert.Equal(Primary.Id, list.ShownId);
    Assert.Equal([new Region(0, 0, 800, 600)], frame);
  }

  [Fact]
  public async Task Then_OnlyWhatChanged_AndNothingWhenNothingDid()
  {
    await StartAsync();
    await NextFrameAsync();
    var captures = _screen.Captures;
    Assert.True(await Until(() => _screen.Captures > captures + 5), "It stopped capturing.");
    Assert.Equal(1, Frames());

    _screen.Paint(Primary, new Region(437, 512, 3, 2));

    Assert.Equal([new Region(437, 512, 3, 2)], await NextFrameAsync());
  }

  [Fact]
  public async Task ChangesTheCaptureReports_AreSent_CutAlongTheSections()
  {
    await StartAsync();
    await NextFrameAsync();

    // Nothing differs in the pixels: the capture's word is taken.
    _screen.Report(new Region(190, 290, 20, 20));

    Assert.Equal(
      [new Region(190, 290, 10, 10), new Region(200, 290, 10, 10), new Region(190, 300, 10, 10), new Region(200, 300, 10, 10)],
      await NextFrameAsync());
  }

  [Fact]
  public async Task WhenAsked_TheWholeScreenComesAgain()
  {
    await StartAsync();
    await NextFrameAsync();

    _streamer.RequestFullFrame();

    Assert.Equal([new Region(0, 0, 800, 600)], await NextFrameAsync());
  }

  [Fact]
  public async Task ShowingAnotherDisplay_SendsItWhole_AndSaysWhichIsShown()
  {
    await StartAsync();
    await NextFrameAsync();

    Assert.False(_streamer.ShowDisplay("three"));
    Assert.True(_streamer.ShowDisplay(Second.Id));

    Assert.Equal([new Region(0, 0, 800, 600)], await NextFrameAsync());
    Assert.Contains(_received, x => x.Type == StreamMessageType.DisplayList && Read<DisplayList>(x).ShownId == Second.Id);
  }

  [Fact]
  public async Task WhenTheDisplaysChange_TheViewerIsTold_AndOneThatWentAway_FallsBackToThePrimary()
  {
    await StartAsync(displayCheck: TimeSpan.FromMilliseconds(50));
    await NextFrameAsync();
    _streamer.ShowDisplay(Second.Id);
    await NextFrameAsync();

    _screen.Displays([Primary]);

    Assert.Equal([new Region(0, 0, 800, 600)], await NextFrameAsync());
    Assert.True(await Until(() => _received.Any(x => x.Type == StreamMessageType.DisplayList && Read<DisplayList>(x) is { Displays.Count: 1, ShownId: "one" })));
  }

  [Fact]
  public async Task AFailedCapture_SendsNothing_AndTheNextFrameIsWhole()
  {
    await StartAsync();
    await NextFrameAsync();
    _screen.Paint(Primary, new Region(1, 1, 1, 1));
    _screen.FailNext(3);

    Assert.Equal([new Region(0, 0, 800, 600)], await NextFrameAsync());
    Assert.Equal(2, Frames());
  }

  [Fact]
  public async Task WhileTheConnectionIsFull_NothingMoreIsCaptured()
  {
    // Nobody reads at the other end, and each frame is larger than what may be in flight.
    await StartAsync(viewerReads: false, imageBytes: RelayStream.MaxUnacknowledgedBytes);
    Assert.True(await Until(() => _screen.Captures > 0));
    await Task.Delay(200, Token);
    var captures = _screen.Captures;

    _screen.Paint(Primary, new Region(0, 0, 10, 10));
    await Task.Delay(300, Token);

    Assert.True(_screen.Captures <= captures + 1, $"{_screen.Captures - captures} captures while the connection was full.");
  }

  public async ValueTask DisposeAsync()
  {
    await _stop.CancelAsync();
    await _streaming.ContinueWith(_ => { }, TaskScheduler.Default);
    _pair?.Dispose();
  }

  private async Task StartAsync(TimeSpan? displayCheck = null, bool viewerReads = true, int imageBytes = 0)
  {
    _pair = await WebSocketPair.ConnectAsync(Token);
    var device = WebSocketPair.Stream(_pair.Client);
    _viewer = WebSocketPair.Stream(_pair.Server);
    _ = device.RunAsync((_, _) => Task.CompletedTask, _stop.Token);
    if (viewerReads)
    {
      _ = _viewer.RunAsync((message, _) => { _received.Enqueue(message); return Task.CompletedTask; }, _stop.Token);
    }

    _streamer = new ScreenStreamer(_screen, (frame, regions, encoding, quality) => Encode(regions, imageBytes), device, TimeProvider.System, NullLogger.Instance)
    {
      DisplayCheckInterval = displayCheck ?? TimeSpan.FromMinutes(1),
    };
    _streaming = _streamer.RunAsync(_stop.Token);
  }

  private static ScreenRegions Encode(IReadOnlyList<Region> regions, int imageBytes) =>
    new([.. regions.Select(x => new ScreenRegion(x.X, x.Y, x.Width, x.Height, new byte[imageBytes], ImageEncoding.Jpeg))]);

  private int Frames() => _received.Count(x => x.Type == StreamMessageType.ScreenRegions);

  // The regions of the next frame the viewer receives after those it already has.
  private int _framesTaken;

  private async Task<IReadOnlyList<Region>> NextFrameAsync()
  {
    var wanted = _framesTaken + 1;
    Assert.True(await Until(() => Frames() >= wanted), $"Frame {wanted} never came.");
    _framesTaken = wanted;
    var frame = Read<ScreenRegions>(_received.Where(x => x.Type == StreamMessageType.ScreenRegions).ElementAt(wanted - 1));
    return [.. frame.Regions.Select(x => new Region(x.X, x.Y, x.Width, x.Height))];
  }

  private static T Read<T>(StreamEnvelope message) => MessagePackSerializer.Deserialize<T>(message.Payload);

  private static async Task<bool> Until(Func<bool> condition)
  {
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline)
    {
      if (condition())
      {
        return true;
      }

      await Task.Delay(10);
    }

    return false;
  }

  /// <summary>Displays whose pixels the test paints; each capture is a copy of how they look then.</summary>
  private sealed class PaintedScreen(IReadOnlyList<DisplayInfo> displays) : IScreenCapture
  {
    private readonly Lock _gate = new();
    private readonly Dictionary<string, byte[]> _pixels = displays.ToDictionary(x => x.Id, x => new byte[x.Width * x.Height * Frame.BytesPerPixel]);
    private IReadOnlyList<DisplayInfo> _displays = displays;
    private Region? _reported;
    private int _failures;
    private int _captures;

    public int Captures => Volatile.Read(ref _captures);

    public void Displays(IReadOnlyList<DisplayInfo> displays)
    {
      lock (_gate)
      {
        _displays = displays;
      }
    }

    public void Paint(DisplayInfo display, Region region)
    {
      lock (_gate)
      {
        var pixels = MemoryMarshal.Cast<byte, uint>(_pixels[display.Id].AsSpan());
        for (var y = region.Y; y < region.Bottom; y++)
        {
          pixels.Slice((y * display.Width) + region.X, region.Width).Fill(0xFFFF0000);
        }
      }
    }

    public void Report(Region region)
    {
      lock (_gate)
      {
        _reported = region;
      }
    }

    public void FailNext(int captures) => Volatile.Write(ref _failures, captures);

    IReadOnlyList<DisplayInfo> IScreenCapture.Displays()
    {
      lock (_gate)
      {
        return _displays;
      }
    }

    public CaptureResult Capture(DisplayInfo display, bool fullFrame)
    {
      Interlocked.Increment(ref _captures);
      if (Interlocked.Decrement(ref _failures) >= 0)
      {
        return CaptureResult.Failed("Painted", "Told to fail.");
      }

      lock (_gate)
      {
        var reported = _reported;
        _reported = null;
        return new CaptureResult(
          new Frame(display.Width, display.Height, (byte[])_pixels[display.Id].Clone()),
          reported is { } region ? [region] : null,
          "Painted");
      }
    }

    public void Dispose()
    {
    }
  }
}
