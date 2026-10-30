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

  [Fact]
  public async Task TheViewersSettings_ChooseTheEncodingAndQuality_FromThenOn()
  {
    await StartAsync();
    await NextFrameAsync();
    Assert.Equal((ImageEncoding.Jpeg, 75), Last());

    _streamer.Apply(new CaptureSettings(Quality: 33, Encoding: ImageEncoding.Png));
    _screen.Paint(Primary, new Region(5, 5, 5, 5));
    await NextFrameAsync();

    Assert.Equal((ImageEncoding.Png, 33), Last());
  }

  [Fact]
  public async Task AboveTheMaximumBandwidth_NothingIsCaptured_UntilItIsRaised()
  {
    // A first frame of 200 KB is more than 0.1 Mbps over the meter's few seconds.
    await StartAsync(imageBytes: 200 * 1024, settings: new CaptureSettings(MaxMbps: 0.1));
    await NextFrameAsync();
    await Task.Delay(100, Token);
    var captures = _screen.Captures;

    _screen.Paint(Primary, new Region(0, 0, 10, 10));
    await Task.Delay(300, Token);
    Assert.Equal(captures, _screen.Captures);

    _streamer.Apply(new CaptureSettings(MaxMbps: null));
    Assert.Equal([new Region(0, 0, 10, 10)], await NextFrameAsync());
  }

  [Fact]
  public async Task WhatWentOutAtALowerQuality_IsSentAgainAtTheBest_OnceTheBandwidthAllows()
  {
    // Every frame weighs 100 KB: the first is sent while nothing was, at the best quality; the next, with that
    // frame on the meter, at the lowest.
    await StartAsync(imageBytes: 100 * 1024, settings: new CaptureSettings(AutoQuality: true, AutoQualityLowerMbps: 0.1, AutoQualityUpperMbps: 0.2));
    await NextFrameAsync();
    Assert.Equal(80, Last().Quality);
    _screen.Paint(Primary, new Region(300, 100, 20, 20));
    await NextFrameAsync();
    Assert.Equal(20, Last().Quality);

    // Once the meter forgets those frames, the region goes again at the best quality, though nothing changed.
    Assert.True(await Until(() => _encoded.Any(x => x.Quality == 80 && x.Regions.Contains(new Region(300, 100, 20, 20))), seconds: 8), "It was never sent again.");
  }

  [Fact]
  public async Task EverySecond_TheViewerIsToldHowTheStreamGoes()
  {
    await StartAsync();
    await NextFrameAsync();

    // The screen keeps changing: frames per second count the last second's frames.
    var x = 0;
    Assert.True(
      await Until(() =>
      {
        _screen.Paint(Primary, new Region(x++ % 700, 10, 2, 2));
        return _received.Any(m => m.Type == StreamMessageType.CaptureMetrics);
      }),
      "No metrics came.");
    var metrics = Read<CaptureMetrics>(_received.First(x => x.Type == StreamMessageType.CaptureMetrics));
    Assert.Equal("Painted", metrics.Mode);
    Assert.Equal(75, metrics.Quality);
    Assert.True(metrics.Fps >= 1, $"Fps was {metrics.Fps}.");
    Assert.True(metrics.MbpsOut > 0);
  }

  [Fact]
  public async Task ThePointersShape_IsSentAtTheStart_AndEachTimeItChanges_Only()
  {
    await StartAsync();
    await NextFrameAsync();

    _screen.CursorKind = CursorKind.IBeam;
    Assert.True(await Until(() => Shapes().Count == 2));
    await Task.Delay(200, Token);

    Assert.Equal([CursorKind.Arrow, CursorKind.IBeam], Shapes());
  }

  [Fact]
  public async Task ThePointer_IsDrawnIntoTheFrames_OnlyWhenTheViewerAsks()
  {
    await StartAsync();
    await NextFrameAsync();
    Assert.False(_screen.DrewCursor);

    _streamer.Apply(new CaptureSettings(ShowCursor: true));

    Assert.True(await Until(() => _screen.DrewCursor));
  }

  private IReadOnlyList<CursorKind> Shapes() =>
    [.. _received.Where(x => x.Type == StreamMessageType.CursorShape).Select(x => Read<CursorShape>(x).Kind)];

  private (ImageEncoding Encoding, int Quality) Last() => _encoded.Last() is var last ? (last.Encoding, last.Quality) : default;

  public async ValueTask DisposeAsync()
  {
    await _stop.CancelAsync();
    await _streaming.ContinueWith(_ => { }, TaskScheduler.Default);
    _pair?.Dispose();
  }

  private async Task StartAsync(TimeSpan? displayCheck = null, bool viewerReads = true, int imageBytes = 0, CaptureSettings? settings = null)
  {
    _pair = await WebSocketPair.ConnectAsync(Token);
    var device = WebSocketPair.Stream(_pair.Client);
    _viewer = WebSocketPair.Stream(_pair.Server);
    _ = device.RunAsync((_, _) => Task.CompletedTask, _stop.Token);
    if (viewerReads)
    {
      _ = _viewer.RunAsync((message, _) => { _received.Enqueue(message); return Task.CompletedTask; }, _stop.Token);
    }

    _streamer = new ScreenStreamer(_screen, (frame, regions, encoding, quality) => Encode(regions, encoding, quality, imageBytes), device, TimeProvider.System, NullLogger.Instance)
    {
      DisplayCheckInterval = displayCheck ?? TimeSpan.FromMinutes(1),
    };
    if (settings is not null)
    {
      _streamer.Apply(settings);
    }

    _streaming = _streamer.RunAsync(_stop.Token);
  }

  // What each frame was encoded with, in order.
  private readonly ConcurrentQueue<(ImageEncoding Encoding, int Quality, Region[] Regions)> _encoded = new();

  private ScreenRegions Encode(IReadOnlyList<Region> regions, ImageEncoding encoding, int quality, int imageBytes)
  {
    _encoded.Enqueue((encoding, quality, [.. regions]));
    return new([.. regions.Select(x => new ScreenRegion(x.X, x.Y, x.Width, x.Height, new byte[imageBytes], encoding))]);
  }

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

  private static async Task<bool> Until(Func<bool> condition, int seconds = 5)
  {
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
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

    /// <summary>The pointer's shape the screen reports.</summary>
    public CursorKind CursorKind { get; set; } = CursorKind.Arrow;

    /// <summary>Whether the last capture was asked to draw the pointer.</summary>
    public bool DrewCursor { get; private set; }

    public CursorKind Cursor() => CursorKind;

    public CaptureResult Capture(DisplayInfo display, bool fullFrame, bool drawCursor)
    {
      DrewCursor = drawCursor;
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
