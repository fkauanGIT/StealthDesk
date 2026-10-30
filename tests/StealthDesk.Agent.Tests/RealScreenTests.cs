using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.Versioning;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.Agent.Tests.Fakes;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.DesktopClient;
using StealthDesk.RemoteControl;

namespace StealthDesk.Agent.Tests;

/// <summary>
/// This machine's real screen, captured with GDI and streamed through one end of a relay stream to the other, where
/// the frames are decoded. Needs Windows and a desktop to capture.
/// </summary>
[SupportedOSPlatform("windows6.1")]
public sealed class RealScreenTests : IDisposable
{
  private readonly List<IDisposable> _disposables = [];
  private readonly CancellationTokenSource _stop = new();

  public RealScreenTests()
  {
    Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(6, 1), "GDI capture is Windows only.");
    Assert.SkipWhen(GdiScreenCapture.ListDisplays().Count == 0, "This session has no display to capture.");
  }

  private static CancellationToken Token => TestContext.Current.CancellationToken;

  [Fact]
  public void TheDisplays_AreListed_InPhysicalPixels_WithOnePrimary()
  {
    var displays = GdiScreenCapture.ListDisplays();

    Assert.Single(displays, x => x.IsPrimary);
    Assert.All(displays, x =>
    {
      Assert.StartsWith(@"\\.\", x.Id, StringComparison.Ordinal);
      Assert.True(x.Width > 0 && x.Height > 0);
    });
    Assert.Equal(displays.Count, displays.Select(x => x.Id).Distinct().Count());
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task TheRealScreen_ArrivesAsFrames_AtTheOtherEnd(bool dxgi)
  {
    var (relay, device) = await LocalWebSockets.ConnectAsync(_disposables, Token);
    var deviceEnd = new RelayStream(device, TimeProvider.System, NullLogger.Instance);
    var viewerEnd = new RelayStream(relay, TimeProvider.System, NullLogger.Instance);
    var received = new ConcurrentQueue<StreamEnvelope>();
    _ = viewerEnd.RunAsync((message, _) => { received.Enqueue(message); return Task.CompletedTask; }, _stop.Token);
    _ = deviceEnd.RunAsync((_, _) => Task.CompletedTask, _stop.Token);
    using IScreenCapture capture = dxgi && OperatingSystem.IsWindowsVersionAtLeast(8) ? new WindowsScreenCapture(NullLogger.Instance) : new GdiScreenCapture();
    var streaming = new ScreenStreamer(capture, FrameEncoder.Encode, deviceEnd, TimeProvider.System, NullLogger.Instance).RunAsync(_stop.Token);

    Assert.True(await Wait.UntilAsync(() => received.Any(x => x.Type == StreamMessageType.ScreenRegions)), "No frame arrived.");

    var list = MessagePackSerializer.Deserialize<DisplayList>(received.First(x => x.Type == StreamMessageType.DisplayList).Payload, cancellationToken: Token);
    var shown = list.Displays.Single(x => x.Id == list.ShownId);
    Assert.True(shown.IsPrimary);
    var frame = MessagePackSerializer.Deserialize<ScreenRegions>(received.First(x => x.Type == StreamMessageType.ScreenRegions).Payload, cancellationToken: Token);
    var whole = Assert.Single(frame.Regions);
    Assert.Equal((0, 0, shown.Width, shown.Height), (whole.X, whole.Y, whole.Width, whole.Height));
    Assert.Equal(ImageEncoding.Jpeg, whole.Encoding);
    using var image = new Bitmap(new MemoryStream(whole.Image));
    Assert.Equal((shown.Width, shown.Height), (image.Width, image.Height));

    await _stop.CancelAsync();
    await streaming;
  }

  [Fact]
  [SupportedOSPlatform("windows8.0")]
  public void DXGI_CapturesThePrimaryDisplay_AtItsSize_AndGivesAWholeFrameWhenAsked_EvenWithNothingNew()
  {
    Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(8), "Desktop duplication needs Windows 8.");
    var primary = GdiScreenCapture.ListDisplays().Single(x => x.IsPrimary);
    var logged = new ListLogger();
    using var capture = new WindowsScreenCapture(logged);

    // The first frame comes once the duplication starts; until then there is nothing new to take.
    CaptureResult? captured = null;
    var deadline = DateTime.UtcNow.AddSeconds(3);
    while (DateTime.UtcNow < deadline && captured is not { Mode: WindowsScreenCapture.Mode, Frame: not null })
    {
      captured = capture.Capture(primary, fullFrame: false);
    }

    Assert.SkipUnless(captured is { Mode: WindowsScreenCapture.Mode }, $"DXGI can't duplicate this session's display: {string.Join(" | ", logged.Messages)}");
    Assert.Equal((primary.Width, primary.Height), (captured!.Frame!.Width, captured.Frame.Height));
    Assert.NotNull(captured.Changed);
    Assert.Null(captured.Error);

    // Most likely nothing changed since: a whole frame must come anyway.
    var whole = capture.Capture(primary, fullFrame: true);
    Assert.NotNull(whole.Frame);
    Assert.Equal((primary.Width, primary.Height), (whole.Frame.Width, whole.Frame.Height));
  }

  public void Dispose()
  {
    _stop.Cancel();
    _disposables.ForEach(x => x.Dispose());
  }

  private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
  {
    public ConcurrentQueue<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
      where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Messages.Enqueue(exception is null ? formatter(state, exception) : $"{formatter(state, exception)} {exception}");
  }
}
