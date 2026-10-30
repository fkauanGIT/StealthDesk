using MessagePack;

namespace StealthDesk.Contracts.RemoteControl;

/// <summary>What a message between the two ends of a remote control session carries.</summary>
public enum StreamMessageType
{
  /// <summary>A <see cref="StreamAck"/>: the other end received a message.</summary>
  Ack = 1,

  /// <summary><see cref="RemoteControl.ScreenRegions"/>, from the desktop client: what changed on the screen shown.</summary>
  ScreenRegions = 2,

  /// <summary><see cref="RemoteControl.DisplayList"/>, from the desktop client: at the start, and whenever it changes.</summary>
  DisplayList = 3,

  /// <summary><see cref="RemoteControl.ChangeDisplay"/>, from the viewer.</summary>
  ChangeDisplay = 4,

  /// <summary><see cref="RemoteControl.RequestFullFrame"/>, from the viewer: send the whole screen next.</summary>
  RequestFullFrame = 5,

  /// <summary><see cref="RemoteControl.CaptureSettings"/>, from the viewer: how to encode what is sent from now on.</summary>
  CaptureSettings = 6,

  /// <summary><see cref="RemoteControl.CaptureMetrics"/>, from the desktop client, every second.</summary>
  CaptureMetrics = 7,

  /// <summary><see cref="RemoteControl.CursorShape"/>, from the desktop client, whenever the pointer changes shape.</summary>
  CursorShape = 8,
}

/// <summary>How the screen is encoded. The defaults apply until the viewer sends its own.</summary>
/// <param name="AutoQuality">Choose the quality from the bandwidth used, instead of <see cref="Quality"/>.</param>
/// <param name="Quality">The JPEG quality, 1 to 100, when it isn't chosen automatically.</param>
/// <param name="AutoQualityMinimum">The quality at <see cref="AutoQualityUpperMbps"/> or more.</param>
/// <param name="AutoQualityMaximum">The quality at <see cref="AutoQualityLowerMbps"/> or less; in between, in proportion.</param>
/// <param name="MaxMbps">At most this much is sent; none for no limit.</param>
/// <param name="ShowCursor">Draw the pointer into the frames, where it is on the device.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record CaptureSettings(
  bool AutoQuality = false,
  int Quality = 75,
  int AutoQualityMinimum = 20,
  int AutoQualityMaximum = 80,
  double AutoQualityLowerMbps = 5,
  double AutoQualityUpperMbps = 15,
  double? MaxMbps = null,
  ImageEncoding Encoding = ImageEncoding.Jpeg,
  bool ShowCursor = false)
{
  /// <summary>The same settings, each in its range and the bounds in order.</summary>
  public CaptureSettings Normalized()
  {
    var minimum = Math.Clamp(AutoQualityMinimum, 1, 99);
    var lower = Math.Max(0.1, AutoQualityLowerMbps);
    return this with
    {
      Quality = Math.Clamp(Quality, 1, 100),
      AutoQualityMinimum = minimum,
      AutoQualityMaximum = Math.Clamp(AutoQualityMaximum, minimum + 1, 100),
      AutoQualityLowerMbps = lower,
      AutoQualityUpperMbps = Math.Max(lower + 0.1, AutoQualityUpperMbps),
      MaxMbps = MaxMbps is { } max ? Math.Max(0.1, max) : null,
    };
  }
}

/// <summary>How the stream is doing, for the viewer to show.</summary>
/// <param name="Fps">Frames sent in the last second.</param>
/// <param name="MbpsOut">What the desktop client sent, averaged over the last few seconds.</param>
/// <param name="Quality">The JPEG quality of the last frame.</param>
/// <param name="Mode">How the screen is captured, e.g. DXGI or GDI.</param>
/// <param name="LatencyMs">The last round trip to the viewer.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record CaptureMetrics(double Fps, double MbpsOut, int Quality, string Mode, double LatencyMs);

/// <summary>One of the device's displays, in physical pixels of the desktop they form together.</summary>
/// <param name="Id">The display's device name, as Windows knows it.</param>
/// <param name="Name">What to show for it.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record DisplayInfo(string Id, string Name, int Left, int Top, int Width, int Height, bool IsPrimary);

/// <summary>The device's displays, and which one is shown.</summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record DisplayList(IReadOnlyList<DisplayInfo> Displays, string ShownId);

/// <summary>Show this display instead; its whole screen comes next.</summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record ChangeDisplay(string Id);

[MessagePackObject(keyAsPropertyName: true)]
public sealed record RequestFullFrame;

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

/// <summary>The shapes of the pointer the viewer can show; any other is <see cref="Unknown"/>.</summary>
public enum CursorKind
{
  Unknown = 0,
  Arrow = 1,
  IBeam = 2,
  Wait = 3,
  SizeNwse = 4,
  SizeNesw = 5,
  SizeWe = 6,
  SizeNs = 7,
  Hand = 8,
}

/// <summary>The pointer's shape on the device, for the viewer to show over the screen.</summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record CursorShape(CursorKind Kind);
