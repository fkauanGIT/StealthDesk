using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace StealthDesk.DesktopClient;

/// <summary>
/// The displays of this session's desktop, and captures of them with GDI: it works everywhere, but copies the whole
/// screen every time and can't tell what changed, so the frames are compared instead.
/// </summary>
[SupportedOSPlatform("windows6.1")]
internal sealed unsafe class GdiScreenCapture : IScreenCapture
{
  public const string Mode = "GDI";

  // Two buffers taken in turn: a frame stays as it was until the capture after next, to be compared with the next one.
  private readonly byte[]?[] _buffers = new byte[2][];
  private int _next;

  public IReadOnlyList<DisplayInfo> Displays() => ListDisplays();

  /// <summary>Every display of the desktop, in physical pixels; the process must be DPI aware for that.</summary>
  public static IReadOnlyList<DisplayInfo> ListDisplays()
  {
    var displays = new List<DisplayInfo>();
    PInvoke.EnumDisplayMonitors(HDC.Null, (RECT?)null, (monitor, _, _, _) => AddDisplay(displays, monitor), default);

    return displays;
  }

  public CursorKind Cursor() => WindowsCursor.Current();

  public CaptureResult Capture(DisplayInfo display, bool fullFrame, bool drawCursor)
  {
    var width = display.Width;
    var height = display.Height;
    var screen = PInvoke.GetDC(HWND.Null);
    if (screen.IsNull)
    {
      return CaptureResult.Failed(Mode, "The screen can't be read.");
    }

    var memory = HDC.Null;
    var bitmap = HBITMAP.Null;
    try
    {
      memory = PInvoke.CreateCompatibleDC(screen);
      var info = new BITMAPINFO();
      info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
      info.bmiHeader.biWidth = width;
      // Negative: rows from the top, as frames are.
      info.bmiHeader.biHeight = -height;
      info.bmiHeader.biPlanes = 1;
      info.bmiHeader.biBitCount = 32;
      info.bmiHeader.biCompression = 0;
      void* bits;
      bitmap = PInvoke.CreateDIBSection(memory, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
      if (bitmap.IsNull)
      {
        return CaptureResult.Failed(Mode, $"No bitmap of {width} x {height} could be made.");
      }

      var previous = PInvoke.SelectObject(memory, bitmap);
      try
      {
        if (!PInvoke.BitBlt(memory, 0, 0, width, height, screen, display.Left, display.Top, ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT))
        {
          return CaptureResult.Failed(Mode, $"Copying the screen failed (Windows error {Marshal.GetLastPInvokeError()}).");
        }

        if (drawCursor)
        {
          WindowsCursor.Draw(memory, display);
        }
      }
      finally
      {
        PInvoke.SelectObject(memory, previous);
      }

      var size = width * height * Frame.BytesPerPixel;
      var buffer = _buffers[_next];
      if (buffer?.Length != size)
      {
        buffer = new byte[size];
        _buffers[_next] = buffer;
      }

      _next = 1 - _next;
      new ReadOnlySpan<byte>(bits, size).CopyTo(buffer);
      return new CaptureResult(new Frame(width, height, buffer), null, Mode);
    }
    finally
    {
      if (!bitmap.IsNull)
      {
        PInvoke.DeleteObject(bitmap);
      }

      if (!memory.IsNull)
      {
        PInvoke.DeleteDC(memory);
      }

      _ = PInvoke.ReleaseDC(HWND.Null, screen);
    }
  }

  public void Dispose()
  {
  }

  private static BOOL AddDisplay(List<DisplayInfo> displays, HMONITOR monitor)
  {
    var info = new MONITORINFOEXW();
    info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
    if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
    {
      return true;
    }

    var rect = info.monitorInfo.rcMonitor;
    displays.Add(new DisplayInfo(
      info.szDevice.ToString(),
      $"Display {displays.Count + 1}",
      rect.left,
      rect.top,
      rect.right - rect.left,
      rect.bottom - rect.top,
      IsPrimary: (info.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0));
    return true;
  }
}
