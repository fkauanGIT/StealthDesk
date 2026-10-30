using MessagePack;

namespace StealthDesk.Contracts.RemoteControl;

/// <summary>What a message between the two ends of a remote control session carries.</summary>
public enum StreamMessageType
{
  /// <summary>A <see cref="StreamAck"/>: the other end received a message.</summary>
  Ack = 1,

  /// <summary><see cref="RemoteControl.ScreenRegions"/>, from the desktop client: what changed on the screen shown.</summary>
  ScreenRegions = 2,
}

/// <summary>How a <see cref="ScreenRegion"/>'s image is encoded.</summary>
public enum ImageEncoding
{
  Jpeg = 0,
  Png = 1,
}

/// <summary>A rectangle of the screen shown, in its pixels, drawn over what the viewer has there.</summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record ScreenRegion(int X, int Y, int Width, int Height, byte[] Image, ImageEncoding Encoding);

/// <summary>The regions that changed in one capture, drawn together.</summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record ScreenRegions(IReadOnlyList<ScreenRegion> Regions);

/// <summary>
/// One message between the viewer and the desktop client on the relay: a binary WebSocket message holding its type,
/// the message itself in MessagePack, and when it was sent, in the sender's own clock.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record StreamEnvelope(StreamMessageType Type, byte[] Payload, long SentAt);

/// <summary>
/// Sent for every message received but an ack: the sender counts what is still in flight by it, and how long the trip
/// took by <see cref="SentAt"/>, returned as it came.
/// </summary>
/// <param name="ReceivedBytes">The size of the message received, as it came on the WebSocket.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record StreamAck(int ReceivedBytes, long SentAt);
