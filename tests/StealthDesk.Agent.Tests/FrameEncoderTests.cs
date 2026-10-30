using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.DesktopClient;
using StealthDesk.RemoteControl;
using Region = StealthDesk.RemoteControl.Region;

namespace StealthDesk.Agent.Tests;

/// <summary>Regions of a frame encoded with GDI+, decoded back to check what they hold. Windows only.</summary>
[SupportedOSPlatform("windows6.1")]
public class FrameEncoderTests
{
  private const uint Blue = 0xFF0000FF;
  private const uint Red = 0xFFFF0000;

  public FrameEncoderTests()
  {
    Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(6, 1), "GDI+ is Windows only.");
  }

  [Fact]
  public void APngRegion_HoldsExactlyThatRegionsPixels()
  {
    var frame = Frame(400, 300);
    Paint(frame, new Region(100, 50, 40, 30), Red);

    var png = FrameEncoder.Encode(frame, new Region(90, 40, 60, 50), ImageEncoding.Png, quality: 100);

    using var image = Decode(png);
    Assert.Equal((60, 50), (image.Width, image.Height));
    Assert.Equal(Color.FromArgb(unchecked((int)Blue)).ToArgb(), image.GetPixel(5, 5).ToArgb());
    Assert.Equal(Color.FromArgb(unchecked((int)Red)).ToArgb(), image.GetPixel(10, 10).ToArgb());
    Assert.Equal(Color.FromArgb(unchecked((int)Red)).ToArgb(), image.GetPixel(49, 39).ToArgb());
    Assert.Equal(Color.FromArgb(unchecked((int)Blue)).ToArgb(), image.GetPixel(50, 40).ToArgb());
  }

  [Fact]
  public void AJpegRegion_IsTheRegionsSize_AndCloseToItsColors()
  {
    var frame = Frame(400, 300);
    Paint(frame, new Region(0, 0, 200, 300), Red);

    var jpeg = FrameEncoder.Encode(frame, new Region(150, 100, 100, 80), ImageEncoding.Jpeg, quality: 90);

    Assert.Equal([0xFF, 0xD8], jpeg[..2]);
    using var image = Decode(jpeg);
    Assert.Equal((100, 80), (image.Width, image.Height));
    var left = image.GetPixel(10, 40);
    var right = image.GetPixel(90, 40);
    Assert.True(left.R > 200 && left.B < 60, $"Expected red on the left, got {left}.");
    Assert.True(right.B > 200 && right.R < 60, $"Expected blue on the right, got {right}.");
  }

  [Fact]
  public void ALowerQuality_MakesASmallerJpeg()
  {
    var frame = Frame(320, 240);
    var random = new Random(7);
    random.NextBytes(frame.Pixels);

    var best = FrameEncoder.Encode(frame, frame.Bounds, ImageEncoding.Jpeg, quality: 95);
    var worst = FrameEncoder.Encode(frame, frame.Bounds, ImageEncoding.Jpeg, quality: 20);

    Assert.True(worst.Length * 2 < best.Length, $"Quality 20 took {worst.Length} bytes, quality 95 {best.Length}.");
  }

  [Fact]
  public void SeveralRegions_AreEncodedInTheirOrder_EachWithItsPlace()
  {
    var frame = Frame(400, 300);
    Region[] regions = [new(0, 0, 10, 10), new(390, 290, 10, 10), new(100, 100, 50, 20)];

    var encoded = FrameEncoder.Encode(frame, regions, ImageEncoding.Png, quality: 80);

    Assert.Equal(regions, encoded.Regions.Select(x => new Region(x.X, x.Y, x.Width, x.Height)));
    Assert.All(encoded.Regions, x => Assert.Equal(ImageEncoding.Png, x.Encoding));
  }

  [Fact]
  public void ARegionOutsideTheFrame_IsRefused()
  {
    Assert.Throws<ArgumentOutOfRangeException>(() => FrameEncoder.Encode(Frame(100, 100), new Region(90, 90, 20, 20), ImageEncoding.Jpeg, 80));
  }

  private static Frame Frame(int width, int height)
  {
    var frame = new Frame(width, height, new byte[width * height * RemoteControl.Frame.BytesPerPixel]);
    Paint(frame, frame.Bounds, Blue);
    return frame;
  }

  private static void Paint(Frame frame, Region region, uint color)
  {
    var pixels = MemoryMarshal.Cast<byte, uint>(frame.Pixels.AsSpan());
    for (var y = region.Y; y < region.Bottom; y++)
    {
      pixels.Slice((y * frame.Width) + region.X, region.Width).Fill(color);
    }
  }

  private static Bitmap Decode(byte[] image) => new(new MemoryStream(image));
}
