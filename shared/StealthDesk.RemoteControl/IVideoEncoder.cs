using StealthDesk.Contracts.RemoteControl;

namespace StealthDesk.RemoteControl;

/// <summary>
/// Turns captured frames into a video stream for the viewer's browser to play. A platform encodes this with
/// whatever it has; the packets it produces are forwarded to the viewer exactly as they come out, unparsed.
/// </summary>
public interface IVideoEncoder : IDisposable
{
  /// <summary>
  /// (Re)starts the encoder for this frame size: whatever it was doing is dropped, and the packets that follow
  /// always open on a key frame. Called at the start of the video mode, and again whenever a key frame is asked
  /// for or the display changes, since restarting is how a key frame is guaranteed.
  /// </summary>
  void Start(int width, int height);

  /// <summary>Queues a captured frame to encode; frames feed a steady rate, so dropping one under load is fine.</summary>
  void EncodeFrame(Frame frame);

  /// <summary>The packets produced, in order, across restarts, until cancelled.</summary>
  IAsyncEnumerable<VideoPacket> Packets(CancellationToken cancellationToken);
}
