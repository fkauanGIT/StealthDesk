using System.Runtime.InteropServices;

namespace StealthDesk.RemoteControl;

/// <summary>A rectangle of a frame, in its pixels.</summary>
public readonly record struct Region(int X, int Y, int Width, int Height)
{
  public int Right => X + Width;

  public int Bottom => Y + Height;

  public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>A captured screen: 32-bit BGRA pixels, row after row with no padding.</summary>
public sealed class Frame
{
  public Frame(int width, int height, byte[] pixels)
  {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
    ArgumentOutOfRangeException.ThrowIfNotEqual(pixels.Length, width * height * BytesPerPixel);
    Width = width;
    Height = height;
    Pixels = pixels;
  }

  public const int BytesPerPixel = 4;

  public int Width { get; }

  public int Height { get; }

  public byte[] Pixels { get; }

  public int Stride => Width * BytesPerPixel;

  public Region Bounds => new(0, 0, Width, Height);

  /// <summary>One row of pixels, each as a single 32-bit value.</summary>
  public ReadOnlySpan<uint> Row(int y) => MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan(y * Stride, Stride));

  /// <summary>
  /// This frame turned clockwise by <paramref name="quarterTurns"/> quarters, into <paramref name="into"/>: as a
  /// rotated display shows what its card draws upright.
  /// </summary>
  public Frame Rotated(int quarterTurns, byte[] into)
  {
    var turns = ((quarterTurns % 4) + 4) % 4;
    var (width, height) = turns % 2 == 0 ? (Width, Height) : (Height, Width);
    var rotated = new Frame(width, height, into);
    var source = MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan());
    var target = MemoryMarshal.Cast<byte, uint>(into.AsSpan());
    for (var y = 0; y < Height; y++)
    {
      for (var x = 0; x < Width; x++)
      {
        var (toX, toY) = turns switch
        {
          0 => (x, y),
          1 => (Height - 1 - y, x),
          2 => (Width - 1 - x, Height - 1 - y),
          _ => (y, Width - 1 - x),
        };
        target[(toY * width) + toX] = source[(y * Width) + x];
      }
    }

    return rotated;
  }
}

/// <summary>
/// What changed between two frames. The frame is split into a grid of sections, and each section that changed gives
/// the smallest rectangle around its changes: a few rectangles, each encoded on its own, instead of one around
/// everything that changed.
/// </summary>
public static class FrameDiff
{
  public const int GridColumns = 4;
  public const int GridRows = 2;

  /// <summary>
  /// The rectangles that changed from <paramref name="previous"/> to <paramref name="current"/>: the whole frame
  /// when there is no previous one or its size differs, none when nothing changed.
  /// </summary>
  public static IReadOnlyList<Region> Changes(Frame current, Frame? previous)
  {
    if (previous is null || previous.Width != current.Width || previous.Height != current.Height)
    {
      return [current.Bounds];
    }

    var changes = new List<Region>();
    foreach (var section in Sections(current.Width, current.Height))
    {
      if (ChangeIn(current, previous, section) is { } changed)
      {
        changes.Add(changed);
      }
    }

    return changes;
  }

  /// <summary>
  /// Rectangles reported as changed, e.g. by the capture itself, cut along the grid's sections and kept inside the
  /// frame; empty ones are left out.
  /// </summary>
  public static IReadOnlyList<Region> AlongSections(int width, int height, IEnumerable<Region> changed)
  {
    var sections = Sections(width, height);
    var cut = new List<Region>();
    foreach (var region in changed)
    {
      foreach (var section in sections)
      {
        var x = Math.Max(region.X, section.X);
        var y = Math.Max(region.Y, section.Y);
        var right = Math.Min(region.Right, section.Right);
        var bottom = Math.Min(region.Bottom, section.Bottom);
        if (x < right && y < bottom)
        {
          cut.Add(new Region(x, y, right - x, bottom - y));
        }
      }
    }

    return cut;
  }

  /// <summary>The grid's sections; the last column and row take what is left over.</summary>
  public static IReadOnlyList<Region> Sections(int width, int height)
  {
    var columns = Math.Min(GridColumns, width);
    var rows = Math.Min(GridRows, height);
    var sectionWidth = width / columns;
    var sectionHeight = height / rows;
    var sections = new List<Region>(columns * rows);
    for (var row = 0; row < rows; row++)
    {
      for (var column = 0; column < columns; column++)
      {
        var x = column * sectionWidth;
        var y = row * sectionHeight;
        var right = column == columns - 1 ? width : x + sectionWidth;
        var bottom = row == rows - 1 ? height : y + sectionHeight;
        sections.Add(new Region(x, y, right - x, bottom - y));
      }
    }

    return sections;
  }

  private static Region? ChangeIn(Frame current, Frame previous, Region section)
  {
    int top = -1, bottom = -1, left = int.MaxValue, right = -1;
    for (var y = section.Y; y < section.Bottom; y++)
    {
      var now = current.Row(y).Slice(section.X, section.Width);
      var before = previous.Row(y).Slice(section.X, section.Width);
      var same = now.CommonPrefixLength(before);
      if (same == section.Width)
      {
        continue;
      }

      top = top < 0 ? y : top;
      bottom = y;
      left = Math.Min(left, same);
      var last = section.Width - 1;
      while (now[last] == before[last])
      {
        last--;
      }

      right = Math.Max(right, last);
    }

    return top < 0
      ? null
      : new Region(section.X + left, top, right - left + 1, bottom - top + 1);
  }
}
