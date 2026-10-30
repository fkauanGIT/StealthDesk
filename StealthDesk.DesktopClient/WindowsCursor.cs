using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace StealthDesk.DesktopClient;

/// <summary>The pointer of this session's desktop: its shape, and drawing it where it is.</summary>
[SupportedOSPlatform("windows6.1")]
internal static unsafe class WindowsCursor
{
  // The system's own cursors, by the resource ids Windows loads them with.
  private static readonly Lazy<Dictionary<nint, CursorKind>> Kinds = new(() => new Dictionary<nint, CursorKind>
  {
    [Load(32512)] = CursorKind.Arrow,
    [Load(32513)] = CursorKind.IBeam,
    [Load(32514)] = CursorKind.Wait,
    [Load(32642)] = CursorKind.SizeNwse,
    [Load(32643)] = CursorKind.SizeNesw,
    [Load(32644)] = CursorKind.SizeWe,
    [Load(32645)] = CursorKind.SizeNs,
    [Load(32649)] = CursorKind.Hand,
  });

  /// <summary>The pointer's shape: one of the system's own, or unknown.</summary>
  public static CursorKind Current() =>
    Info() is { } info && Kinds.Value.TryGetValue((nint)info.hCursor.Value, out var kind) ? kind : CursorKind.Unknown;

  /// <summary>Draws the pointer onto <paramref name="context"/>, which shows <paramref name="display"/>; nothing when it is hidden.</summary>
  public static void Draw(HDC context, DisplayInfo display)
  {
    if (Info() is not { } info || (info.flags & CURSORINFO_FLAGS.CURSOR_SHOWING) == 0)
    {
      return;
    }

    ICONINFO icon;
    var (hotspotX, hotspotY) = (0, 0);
    if (PInvoke.GetIconInfo(new HICON(info.hCursor.Value), &icon))
    {
      (hotspotX, hotspotY) = ((int)icon.xHotspot, (int)icon.yHotspot);
      // The icon's bitmaps are copies the caller owns.
      PInvoke.DeleteObject(icon.hbmMask);
      PInvoke.DeleteObject(icon.hbmColor);
    }

    PInvoke.DrawIconEx(
      context,
      info.ptScreenPos.X - display.Left - hotspotX,
      info.ptScreenPos.Y - display.Top - hotspotY,
      new HICON(info.hCursor.Value),
      0,
      0,
      0,
      HBRUSH.Null,
      DI_FLAGS.DI_NORMAL);
  }

  /// <summary>Draws the pointer into a frame of <paramref name="display"/>.</summary>
  public static void Draw(Frame frame, DisplayInfo display)
  {
    var pinned = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
    try
    {
      using var bitmap = new Bitmap(frame.Width, frame.Height, frame.Stride, PixelFormat.Format32bppRgb, pinned.AddrOfPinnedObject());
      using var graphics = Graphics.FromImage(bitmap);
      var context = graphics.GetHdc();
      try
      {
        Draw(new HDC(context), display);
      }
      finally
      {
        graphics.ReleaseHdc(context);
      }
    }
    finally
    {
      pinned.Free();
    }
  }

  /// <summary>Where the pointer is on the desktop, or none when it is hidden.</summary>
  public static (int X, int Y)? Position() =>
    Info() is { } info && (info.flags & CURSORINFO_FLAGS.CURSOR_SHOWING) != 0 ? (info.ptScreenPos.X, info.ptScreenPos.Y) : null;

  private static CURSORINFO? Info()
  {
    var info = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
    return PInvoke.GetCursorInfo(ref info) && !info.hCursor.IsNull ? info : null;
  }

  private static nint Load(int id) => (nint)PInvoke.LoadCursor(HINSTANCE.Null, (char*)id).Value;
}
