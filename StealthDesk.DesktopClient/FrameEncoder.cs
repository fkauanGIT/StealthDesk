using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl;
using Region = StealthDesk.RemoteControl.Region;

namespace StealthDesk.DesktopClient;

/// <summary>Regions of a frame as images, with GDI+: JPEG at a chosen quality, or PNG, which is lossless.</summary>
[SupportedOSPlatform("windows6.1")]
internal static class FrameEncoder
{
  private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);

  /// <summary>Each region encoded on its own, in parallel.</summary>
  /// <param name="quality">1 to 100; JPEG only.</param>
  public static ScreenRegions Encode(Frame frame, IReadOnlyList<Region> regions, ImageEncoding encoding, int quality)
  {
    var encoded = new ScreenRegion[regions.Count];
    Parallel.For(0, regions.Count, index =>
    {
      var region = regions[index];
      encoded[index] = new ScreenRegion(region.X, region.Y, region.Width, region.Height, Encode(frame, region, encoding, quality), encoding);
    });
    return new ScreenRegions(encoded);
  }

  public static byte[] Encode(Frame frame, Region region, ImageEncoding encoding, int quality)
  {
    if (region.X < 0 || region.Y < 0 || region.IsEmpty || region.Right > frame.Width || region.Bottom > frame.Height)
    {
      throw new ArgumentOutOfRangeException(nameof(region), region, "The region isn't inside the frame.");
    }

    // The bitmap reads the region straight from the frame's pixels, a row's stride apart: nothing is copied.
    var pinned = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
    try
    {
      var start = pinned.AddrOfPinnedObject() + (region.Y * frame.Stride) + (region.X * Frame.BytesPerPixel);
      using var bitmap = new Bitmap(region.Width, region.Height, frame.Stride, PixelFormat.Format32bppRgb, start);
      using var output = new MemoryStream();
      if (encoding == ImageEncoding.Png)
      {
        bitmap.Save(output, ImageFormat.Png);
      }
      else
      {
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
        bitmap.Save(output, Jpeg, parameters);
      }

      return output.ToArray();
    }
    finally
    {
      pinned.Free();
    }
  }
}
