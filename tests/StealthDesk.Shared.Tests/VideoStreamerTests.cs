using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl;

namespace StealthDesk.Shared.Tests;

/// <summary>
/// Streaming a display to the viewer as video: idle until the viewer chooses the video mode, then feeding captured
/// frames to an encoder and forwarding whatever it produces. A key frame, which only a restart guarantees, is asked
/// for at the start, on request, and whenever the display shown changes.
/// </summary>
public sealed class VideoStreamerTests : IAsyncDisposable
{
  private static readonly DisplayInfo Primary = new("one", "Display 1", 0, 0, 800, 600, IsPrimary: true);
  private static readonly DisplayInfo Second = new("two", "Display 2", 800, 0, 640, 480, IsPrimary: false);

  private readonly RecordingEncoder _encoder = new();
  private readonly CaptureModeSwitch _mode = new();
  private readonly ConcurrentQueue<StreamEnvelope> _received = new();
  private readonly CancellationTokenSource _stop = new();
  private DisplayInfo? _shown = Primary;
  private WebSocketPair _pair = null!;
  private VideoStreamer _streamer = null!;
  private Task _streaming = Task.CompletedTask;

  private static CancellationToken Token => TestContext.Current.CancellationToken;

  [Fact]
  public async Task WhileRegionsAreChosen_NothingIsCaptured_OrSent()
  {
    await StartAsync();

    await Task.Delay(200, Token);

    Assert.Equal(0, _encoder.Starts);
    Assert.Empty(_received);
  }

  [Fact]
  public async Task OnceVideoIsChosen_TheEncoderStarts_ForTheShownDisplaysSize_AndFramesAreFed()
  {
    await StartAsync();

    _mode.Mode = CaptureMode.Video;

    Assert.True(await Until(() => _encoder.Starts >= 1));
    Assert.Equal((Primary.Width, Primary.Height), _encoder.LastStartSize);
    Assert.True(await Until(() => _encoder.FramesEncoded >= 2), "No frames reached the encoder.");
  }

  [Fact]
  public async Task WhatTheEncoderProduces_ReachesTheViewer_AsVideoPackets()
  {
    await StartAsync();
    _mode.Mode = CaptureMode.Video;

    Assert.True(await Until(() => _received.Any(x => x.Type == StreamMessageType.VideoPacket)));
    var packet = Read<VideoPacket>(_received.First(x => x.Type == StreamMessageType.VideoPacket));
    Assert.True(packet.IsInit);
    Assert.Equal([1, 2, 3], packet.Data);
  }

  [Fact]
  public async Task ARequestedKeyFrame_RestartsTheEncoder()
  {
    await StartAsync();
    _mode.Mode = CaptureMode.Video;
    Assert.True(await Until(() => _encoder.Starts >= 1));

    _streamer.RequestKeyFrame();

    Assert.True(await Until(() => _encoder.Starts >= 2));
  }

  [Fact]
  public async Task TheDisplayChanging_RestartsTheEncoder_ForItsOwnSize()
  {
    await StartAsync();
    _mode.Mode = CaptureMode.Video;
    Assert.True(await Until(() => _encoder.Starts >= 1));

    _shown = Second;

    Assert.True(await Until(() => _encoder.LastStartSize == (Second.Width, Second.Height)));
  }

  public async ValueTask DisposeAsync()
  {
    await _stop.CancelAsync();
    await _streaming.ContinueWith(_ => { }, TaskScheduler.Default);
    _pair?.Dispose();
  }

  private async Task StartAsync()
  {
    _pair = await WebSocketPair.ConnectAsync(Token);
    var device = WebSocketPair.Stream(_pair.Client);
    var viewer = WebSocketPair.Stream(_pair.Server);
    _ = device.RunAsync((_, _) => Task.CompletedTask, _stop.Token);
    _ = viewer.RunAsync((message, _) => { _received.Enqueue(message); return Task.CompletedTask; }, _stop.Token);

    _streamer = new VideoStreamer(new NoopCapture(), _encoder, device, TimeProvider.System, () => _shown, () => false, _mode, NullLogger.Instance);
    _streaming = _streamer.RunAsync(_stop.Token);
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

  /// <summary>A capture that always returns the same blank frame, whatever display is asked for.</summary>
  private sealed class NoopCapture : IScreenCapture
  {
    public IReadOnlyList<DisplayInfo> Displays() => [Primary, Second];

    public CaptureResult Capture(DisplayInfo display, bool fullFrame, bool drawCursor) =>
      new(new Frame(display.Width, display.Height, new byte[display.Width * display.Height * Frame.BytesPerPixel]), null, "Noop");

    public CursorKind Cursor() => CursorKind.Arrow;

    public void Dispose()
    {
    }
  }

  /// <summary>An encoder a test can inspect: it emits one fixed packet, marked init, every time it (re)starts.</summary>
  private sealed class RecordingEncoder : IVideoEncoder
  {
    private readonly ConcurrentQueue<VideoPacket> _packets = new();
    private readonly SemaphoreSlim _signal = new(0);

    public int Starts { get; private set; }

    public int FramesEncoded { get; private set; }

    public (int Width, int Height)? LastStartSize { get; private set; }

    public void Start(int width, int height)
    {
      Starts++;
      LastStartSize = (width, height);
      _packets.Enqueue(new VideoPacket([1, 2, 3], IsInit: true));
      _signal.Release();
    }

    public void EncodeFrame(Frame frame) => FramesEncoded++;

    public async IAsyncEnumerable<VideoPacket> Packets([EnumeratorCancellation] CancellationToken cancellationToken)
    {
      while (true)
      {
        await _signal.WaitAsync(cancellationToken);
        while (_packets.TryDequeue(out var packet))
        {
          yield return packet;
        }
      }
    }

    public void Dispose()
    {
    }
  }
}
