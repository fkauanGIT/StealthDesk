using StealthDesk.Contracts.RemoteControl;

namespace StealthDesk.RemoteControl.Input;

/// <summary>Turns a point given as a fraction of the shown display into a pixel on the device's desktop.</summary>
public static class PointerMath
{
  /// <summary>
  /// The pixel on <paramref name="display"/> for the fractions <paramref name="x"/> and <paramref name="y"/> (0 to 1),
  /// in the desktop's coordinates. Out-of-range fractions are brought back to the display's edges, so a drag that
  /// leaves the picture lands on its border rather than on another display.
  /// </summary>
  public static (int X, int Y) ToPixel(DisplayInfo display, double x, double y)
  {
    var px = display.Left + (int)Math.Round(Math.Clamp(x, 0, 1) * Math.Max(0, display.Width - 1));
    var py = display.Top + (int)Math.Round(Math.Clamp(y, 0, 1) * Math.Max(0, display.Height - 1));
    return (px, py);
  }
}
