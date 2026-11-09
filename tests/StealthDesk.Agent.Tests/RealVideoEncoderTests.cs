using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.DesktopClient;
using StealthDesk.RemoteControl;

namespace StealthDesk.Agent.Tests;

/// <summary>
/// The real video encoder on this machine: an actual ffmpeg process fed real frames, producing an actual VP9/WebM
/// stream, without a real screen behind it. Skipped where ffmpeg isn't installed, since this client doesn't bundle
/// it for tests.
/// </summary>
public sealed class RealVideoEncoderTests : IDisposable
{
  private readonly FfmpegVideoEncoder _encoder;

  public RealVideoEncoderTests()
  {
    var ffmpegPath = FfmpegPath.Resolve();
    Assert.SkipUnless(IsAvailable(ffmpegPath), "Needs ffmpeg.");
    _encoder = new FfmpegVideoEncoder(ffmpegPath, NullLogger<FfmpegVideoEncoder>.Instance);
  }

  [Fact]
  public async Task StartingTheEncoder_AndFeedingItFrames_ProducesAPacketMarkedInit()
  {
    var packets = _encoder.Packets(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
    _encoder.Start(64, 64);
    FeedFrames(10);

    Assert.True(await packets.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken), "No packet arrived.");
    Assert.True(packets.Current.IsInit);
    Assert.NotEmpty(packets.Current.Data);
  }

  [Fact]
  public async Task RestartingTheEncoder_SendsAFreshInitPacket_Again()
  {
    var packets = _encoder.Packets(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
    _encoder.Start(64, 64);
    FeedFrames(5);
    Assert.True(await packets.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
    Assert.True(packets.Current.IsInit);

    _encoder.Start(64, 64);
    FeedFrames(5);

    // The dying process's last buffered bytes may still arrive first; keep going until a second init packet does.
    var sawSecondInit = false;
    for (var i = 0; i < 100 && !sawSecondInit; i++)
    {
      if (!await packets.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken))
      {
        break;
      }

      sawSecondInit = packets.Current.IsInit;
    }

    Assert.True(sawSecondInit, "No second init packet arrived after restarting.");
  }

  public void Dispose() => _encoder.Dispose();

  private void FeedFrames(int count)
  {
    var pixels = new byte[64 * 64 * Frame.BytesPerPixel];
    for (var i = 0; i < count; i++)
    {
      _encoder.EncodeFrame(new Frame(64, 64, pixels));
    }
  }

  private static bool IsAvailable(string path)
  {
    try
    {
      using var process = Process.Start(new ProcessStartInfo(path, "-version")
      {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      });
      return process is not null && process.WaitForExit(3000) && process.ExitCode == 0;
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
      return false;
    }
  }
}
