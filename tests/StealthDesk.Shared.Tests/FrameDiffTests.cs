using StealthDesk.RemoteControl;

namespace StealthDesk.Shared.Tests;

/// <summary>What changed between two frames, per section of the grid, without a real screen.</summary>
public class FrameDiffTests
{
  private const uint Blue = 0xFF0000FF;
  private const uint Red = 0xFFFF0000;

  [Fact]
  public void WithNoPreviousFrame_OrOfAnotherSize_TheWholeFrameChanged()
  {
    var frame = Filled(800, 600, Blue);

    Assert.Equal([frame.Bounds], FrameDiff.Changes(frame, previous: null));
    Assert.Equal([frame.Bounds], FrameDiff.Changes(frame, Filled(1024, 768, Blue)));
  }

  [Fact]
  public void TheSameFrame_HasNoChanges()
  {
    Assert.Empty(FrameDiff.Changes(Filled(800, 600, Blue), Filled(800, 600, Blue)));
  }

  [Fact]
  public void OnePixel_IsARegionOfOnePixel_WhereItIs()
  {
    var previous = Filled(800, 600, Blue);
    var current = Filled(800, 600, Blue);
    Paint(current, new Region(437, 512, 1, 1), Red);

    Assert.Equal([new Region(437, 512, 1, 1)], FrameDiff.Changes(current, previous));
  }

  [Fact]
  public void ChangesInTwoSections_AreTwoRegions_EachJustAroundItsChanges()
  {
    var previous = Filled(800, 600, Blue);
    var current = Filled(800, 600, Blue);
    Paint(current, new Region(10, 20, 30, 40), Red);
    Paint(current, new Region(610, 400, 50, 5), Red);

    Assert.Equal([new Region(10, 20, 30, 40), new Region(610, 400, 50, 5)], FrameDiff.Changes(current, previous));
  }

  [Fact]
  public void AChangeAcrossSections_IsCutAlongThem()
  {
    var previous = Filled(800, 600, Blue);
    var current = Filled(800, 600, Blue);
    // Sections are 200 x 300: this crosses the line between the first two columns and the two rows.
    Paint(current, new Region(190, 290, 20, 20), Red);

    Assert.Equal(
      [new Region(190, 290, 10, 10), new Region(200, 290, 10, 10), new Region(190, 300, 10, 10), new Region(200, 300, 10, 10)],
      FrameDiff.Changes(current, previous));
  }

  [Fact]
  public void ADiagonalChange_IsBoundedByItsFirstAndLastPixels_OnEveryRow()
  {
    var previous = Filled(800, 600, Blue);
    var current = Filled(800, 600, Blue);
    Paint(current, new Region(50, 10, 1, 1), Red);
    Paint(current, new Region(20, 30, 1, 1), Red);
    Paint(current, new Region(90, 60, 1, 1), Red);

    Assert.Equal([new Region(20, 10, 71, 51)], FrameDiff.Changes(current, previous));
  }

  [Theory]
  [InlineData(1920, 1080)]
  [InlineData(1366, 767)]
  [InlineData(3, 1)]
  public void TheSections_CoverTheFrameExactly(int width, int height)
  {
    var sections = FrameDiff.Sections(width, height);

    Assert.Equal((long)width * height, sections.Sum(x => (long)x.Width * x.Height));
    Assert.All(sections, x => Assert.False(x.IsEmpty));
    Assert.Equal(width, sections.Max(x => x.Right));
    Assert.Equal(height, sections.Max(x => x.Bottom));
  }

  [Fact]
  public void ReportedChanges_AreCutAlongTheSections_AndKeptInsideTheFrame()
  {
    var cut = FrameDiff.AlongSections(800, 600, [new Region(150, 250, 100, 100), new Region(780, 580, 50, 50), new Region(10, 10, 0, 5)]);

    Assert.Equal(
      [
        new Region(150, 250, 50, 50), new Region(200, 250, 50, 50), new Region(150, 300, 50, 50), new Region(200, 300, 50, 50),
        new Region(780, 580, 20, 20),
      ],
      cut);
  }

  [Theory]
  [InlineData(0, 3, 2, new uint[] { 1, 2, 3, 4, 5, 6 })]
  [InlineData(1, 2, 3, new uint[] { 4, 1, 5, 2, 6, 3 })]
  [InlineData(2, 3, 2, new uint[] { 6, 5, 4, 3, 2, 1 })]
  [InlineData(3, 2, 3, new uint[] { 3, 6, 2, 5, 1, 4 })]
  [InlineData(5, 2, 3, new uint[] { 4, 1, 5, 2, 6, 3 })]
  public void AFrame_TurnsClockwise_ByQuarters(int quarterTurns, int width, int height, uint[] expected)
  {
    // 1 2 3
    // 4 5 6
    uint[] pixels = [1, 2, 3, 4, 5, 6];
    var frame = new Frame(3, 2, System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray());

    var turned = frame.Rotated(quarterTurns, new byte[pixels.Length * Frame.BytesPerPixel]);

    Assert.Equal((width, height), (turned.Width, turned.Height));
    Assert.Equal(expected, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(turned.Pixels.AsSpan()).ToArray());
  }

  private static Frame Filled(int width, int height, uint color)
  {
    var frame = new Frame(width, height, new byte[width * height * Frame.BytesPerPixel]);
    Paint(frame, frame.Bounds, color);
    return frame;
  }

  private static void Paint(Frame frame, Region region, uint color)
  {
    var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(frame.Pixels.AsSpan());
    for (var y = region.Y; y < region.Bottom; y++)
    {
      pixels.Slice((y * frame.Width) + region.X, region.Width).Fill(color);
    }
  }
}
