using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl;

namespace StealthDesk.DesktopClient;

/// <summary>Where the ffmpeg this client encodes video with is found.</summary>
internal static class FfmpegPath
{
  /// <summary>The copy published next to this executable, or "ffmpeg" to use whatever the machine's PATH has.</summary>
  public static string Resolve()
  {
    var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
    return File.Exists(bundled) ? bundled : "ffmpeg";
  }
}

/// <summary>
/// Encodes frames into a raw VP9 stream with ffmpeg, run as a child process: frames go in its standard input raw, as
/// this client already has them (32-bit BGRA, which is also what ffmpeg is told to expect, so nothing is converted),
/// and the IVF container on its standard output is parsed frame by frame, so each <see cref="VideoPacket"/> is
/// exactly one encoded frame the browser's <c>VideoDecoder</c> can decode on its own, with no buffering container
/// (WebM/MediaSource) in between. A fresh process is started on every <see cref="Start"/>, which is the only way
/// this ties a key frame to a moment: ffmpeg doesn't take a live command for one, but it always opens a new encode
/// with one, and <c>-g</c> below keeps it the only one for that process's whole life.
/// </summary>
internal sealed class FfmpegVideoEncoder(string ffmpegPath, ILogger<FfmpegVideoEncoder> logger) : IVideoEncoder
{
  // Large enough that libvpx never inserts a second key frame on its own; a fresh one only ever comes from Start().
  private const int KeyFrameIntervalFrames = 100_000;

  private readonly Channel<VideoPacket> _packets = Channel.CreateUnbounded<VideoPacket>(new UnboundedChannelOptions { SingleReader = true });
  private readonly Lock _gate = new();
  private Process? _process;

  public void Start(int width, int height)
  {
    lock (_gate)
    {
      StopProcess();
      try
      {
        _process = StartProcess(width, height);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Starting the video encoder failed; the video mode won't send anything until it can.");
        _process = null;
      }
    }
  }

  public void EncodeFrame(Frame frame)
  {
    Process? process;
    lock (_gate)
    {
      process = _process;
    }

    if (process is null)
    {
      return;
    }

    try
    {
      process.StandardInput.BaseStream.Write(frame.Pixels);
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
    {
      logger.LogDebug(ex, "Writing a frame to the video encoder failed; it restarts on the next key frame.");
    }
  }

  public IAsyncEnumerable<VideoPacket> Packets(CancellationToken cancellationToken) => _packets.Reader.ReadAllAsync(cancellationToken);

  public void Dispose()
  {
    lock (_gate)
    {
      StopProcess();
    }

    _packets.Writer.TryComplete();
  }

  private Process StartProcess(int width, int height)
  {
    var info = new ProcessStartInfo(ffmpegPath)
    {
      // Raw BGRA frames in, at this frame size, over stdin; a raw VP9 stream out over stdout, in the IVF container
      // (a plain sequence of frame headers, unlike WebM/Matroska, so ffmpeg writes each frame out as it is encoded
      // instead of batching several into a cluster). -lag-in-frames 0 turns off libvpx's own look-ahead buffering,
      // which otherwise holds frames back for better compression at the cost of latency we don't want here.
      Arguments = $"-f rawvideo -pixel_format bgra -video_size {width}x{height} -i pipe:0 " +
        $"-c:v libvpx-vp9 -deadline realtime -cpu-used 8 -row-mt 1 -lag-in-frames 0 -g {KeyFrameIntervalFrames} " +
        "-pix_fmt yuv420p -f ivf pipe:1",
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };

    var process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg didn't start.");
    _ = DrainErrorAsync(process);
    _ = PumpOutputAsync(process);
    return process;
  }

  // The IVF container: a 32-byte file header, then one 12-byte frame header (4-byte little-endian size, 8-byte
  // timestamp, both unused here) followed by that many bytes of raw VP9 frame data, repeated until the stream ends.
  private async Task PumpOutputAsync(Process process)
  {
    var stdout = process.StandardOutput.BaseStream;
    var frameHeader = new byte[12];
    try
    {
      await stdout.ReadExactlyAsync(new byte[32]);
      var isFirstFrame = true;
      while (true)
      {
        await stdout.ReadExactlyAsync(frameHeader);
        var frame = new byte[BinaryPrimitives.ReadUInt32LittleEndian(frameHeader)];
        await stdout.ReadExactlyAsync(frame);

        lock (_gate)
        {
          // Another Start() may already have moved on: a dead process's last frames are dropped, not sent.
          if (_process != process)
          {
            return;
          }
        }

        await _packets.Writer.WriteAsync(new VideoPacket(frame, isFirstFrame));
        isFirstFrame = false;
      }
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException)
    {
      // The process ended, or Start() replaced it.
    }
  }

  private async Task DrainErrorAsync(Process process)
  {
    try
    {
      // Never logged: ffmpeg's own progress chatter on stderr isn't an error, and this just keeps the pipe from filling.
      await process.StandardError.BaseStream.CopyToAsync(Stream.Null);
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
    {
      // The process ended.
    }
  }

  private void StopProcess()
  {
    if (_process is not { } old)
    {
      return;
    }

    _process = null;
    try
    {
      old.Kill(entireProcessTree: true);
    }
    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
    {
      // Already gone.
    }

    old.Dispose();
  }
}
