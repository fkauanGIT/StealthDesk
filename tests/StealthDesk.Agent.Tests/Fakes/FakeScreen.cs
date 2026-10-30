using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl;

namespace StealthDesk.Agent.Tests.Fakes;

/// <summary>Two displays of a single color each, captured as they are, without a real screen.</summary>
internal sealed class FakeScreen : IScreenCapture
{
  public static readonly DisplayInfo Primary = new(@"\\.\DISPLAY1", "Display 1", 0, 0, 64, 48, IsPrimary: true);
  public static readonly DisplayInfo Second = new(@"\\.\DISPLAY2", "Display 2", 64, 0, 32, 24, IsPrimary: false);

  public IReadOnlyList<DisplayInfo> Displays() => [Primary, Second];

  public CaptureResult Capture(DisplayInfo display, bool fullFrame) =>
    new(new Frame(display.Width, display.Height, new byte[display.Width * display.Height * Frame.BytesPerPixel]), null, "Fake");

  public void Dispose()
  {
  }

  /// <summary>Each region as an empty image: what matters is where it is.</summary>
  public static ScreenRegions Encode(Frame frame, IReadOnlyList<Region> regions, ImageEncoding encoding, int quality) =>
    new([.. regions.Select(x => new ScreenRegion(x.X, x.Y, x.Width, x.Height, [], encoding))]);
}
