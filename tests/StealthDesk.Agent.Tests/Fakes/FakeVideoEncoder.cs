using System.Threading.Channels;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl;

namespace StealthDesk.Agent.Tests.Fakes;

/// <summary>A video encoder a test can inspect, instead of a real ffmpeg process.</summary>
internal sealed class FakeVideoEncoder : IVideoEncoder
{
  private readonly Channel<VideoPacket> _packets = Channel.CreateUnbounded<VideoPacket>();

  public int Starts { get; private set; }

  public int FramesEncoded { get; private set; }

  public (int Width, int Height)? LastStartSize { get; private set; }

  public void Start(int width, int height)
  {
    Starts++;
    LastStartSize = (width, height);
    _packets.Writer.TryWrite(new VideoPacket([1], IsInit: true));
  }

  public void EncodeFrame(Frame frame) => FramesEncoded++;

  public IAsyncEnumerable<VideoPacket> Packets(CancellationToken cancellationToken) => _packets.Reader.ReadAllAsync(cancellationToken);

  public void Dispose() => _packets.Writer.TryComplete();
}
