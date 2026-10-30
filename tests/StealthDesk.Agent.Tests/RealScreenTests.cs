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

  [Fact]
  public async Task TheRealScreen_ArrivesAsFrames_AtTheOtherEnd()
  {
    var (relay, device) = await LocalWebSockets.ConnectAsync(_disposables, Token);
    var deviceEnd = new RelayStream(device, TimeProvider.System, NullLogger.Instance);
    var viewerEnd = new RelayStream(relay, TimeProvider.System, NullLogger.Instance);
    var received = new ConcurrentQueue<StreamEnvelope>();
    _ = viewerEnd.RunAsync((message, _) => { received.Enqueue(message); return Task.CompletedTask; }, _stop.Token);
    _ = deviceEnd.RunAsync((_, _) => Task.CompletedTask, _stop.Token);
    using var capture = new GdiScreenCapture();
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

  public void Dispose()
  {
    _stop.Cancel();
    _disposables.ForEach(x => x.Dispose());
  }
}
