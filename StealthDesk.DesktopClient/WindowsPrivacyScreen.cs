using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using StealthDesk.RemoteControl;

namespace StealthDesk.DesktopClient;

/// <summary>Always answers false: no privacy screen below the Windows version that can exclude one from capture.</summary>
internal sealed class NoPrivacyScreen : IPrivacyScreen
{
  public bool SetActive(bool active) => false;
}

/// <summary>
/// An opaque black window covering every display, excluded from this process's own screen capture
/// (<c>WDA_EXCLUDEFROMCAPTURE</c>), so a remote control session's viewer keeps seeing the real desktop while the
/// person at the device sees only black. The window never takes a click or the focus (<c>WM_NCHITTEST</c> answers
/// <c>HTTRANSPARENT</c>), so real and injected input alike still reach the desktop underneath it. It is shown only
/// once the exclusion is confirmed, so it never hides the desktop from the device without hiding it from the viewer
/// too.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
internal sealed class WindowsPrivacyScreen : IPrivacyScreen, IDisposable
{
  private const uint WM_APP = 0x8000;
  private const uint ToggleMessage = WM_APP + 1;
  private const uint WM_NCHITTEST = 0x0084;
  private const uint WM_MOUSEACTIVATE = 0x0021;
  private const uint WM_CLOSE = 0x0010;
  private const uint WM_DESTROY = 0x0002;
  private const nint HTTRANSPARENT = -1;
  private const nint MA_NOACTIVATE = 3;

  private const uint CS_OWNDC = 0x0020;
  private const uint WS_POPUP = 0x80000000;
  private const uint WS_EX_TOPMOST = 0x00000008;
  private const uint WS_EX_TOOLWINDOW = 0x00000080;
  private const uint WS_EX_NOACTIVATE = 0x08000000;
  private const uint WS_EX_TRANSPARENT = 0x00000020;
  private const uint SWP_NOZORDER = 0x0004;
  private const uint SWP_SHOWWINDOW = 0x0040;
  private const int SW_HIDE = 0;
  private const int SM_XVIRTUALSCREEN = 76;
  private const int SM_YVIRTUALSCREEN = 77;
  private const int SM_CXVIRTUALSCREEN = 78;
  private const int SM_CYVIRTUALSCREEN = 79;
  private const uint WDA_NONE = 0x00000000;
  private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

  private const string ClassName = "StealthDeskPrivacyScreen";

  // Kept alive for the process's lifetime: native code calls back into it as long as the window class exists.
  private static readonly WndProc Procedure = WindowProc;

  // Set on the window's own thread right before Show reports failure; read from the caller right after SendMessage
  // returns, which only happens once that thread finished handling it.
  private static int _lastShowError;

  private readonly ILogger<WindowsPrivacyScreen> _logger;
  private readonly Lock _gate = new();
  private nint _window;
  private bool _active;

  public WindowsPrivacyScreen(ILogger<WindowsPrivacyScreen> logger) => _logger = logger;

  public bool SetActive(bool active)
  {
    lock (_gate)
    {
      if (active == _active)
      {
        return _active;
      }

      if (_window == 0)
      {
        if (!active)
        {
          return false;
        }

        Start();
      }

      if (_window == 0)
      {
        return false;
      }

      var result = SendMessage(_window, ToggleMessage, active ? 1 : 0, 0);
      _active = result != 0;
      if (active && !_active)
      {
        _logger.LogWarning("The privacy screen's exclusion from capture couldn't be set (Windows error {Error}).", _lastShowError);
      }

      return _active;
    }
  }

  public void Dispose()
  {
    lock (_gate)
    {
      if (_window != 0)
      {
        PostMessage(_window, WM_CLOSE, 0, 0);
        _window = 0;
      }

      _active = false;
    }
  }

  // A window may only be touched by the thread that created it, so it gets a dedicated thread with its own message
  // loop, for as long as the process runs.
  private void Start()
  {
    using var ready = new ManualResetEventSlim();
    var thread = new Thread(() => Run(ready)) { IsBackground = true };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    ready.Wait(TimeSpan.FromSeconds(2));
  }

  private void Run(ManualResetEventSlim ready)
  {
    try
    {
      var wndClass = new WNDCLASSEX
      {
        cbSize = Marshal.SizeOf<WNDCLASSEX>(),
        style = CS_OWNDC,
        lpfnWndProc = Procedure,
        hbrBackground = CreateSolidBrush(0),
        lpszClassName = ClassName,
      };
      if (RegisterClassEx(ref wndClass) == 0)
      {
        _logger.LogWarning("The privacy screen's window class couldn't be registered (Windows error {Error}).", Marshal.GetLastWin32Error());
        return;
      }

      // Not under _gate: the only caller, SetActive, already holds it for this whole call, blocked in Start().
      _window = CreateWindowEx(
        WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
        ClassName,
        string.Empty,
        WS_POPUP,
        0, 0, 0, 0,
        0, 0, 0, 0);

      if (_window == 0)
      {
        _logger.LogWarning("The privacy screen's window couldn't be created (Windows error {Error}).", Marshal.GetLastWin32Error());
        return;
      }
    }
    finally
    {
      ready.Set();
    }

    while (GetMessage(out var message, 0, 0, 0))
    {
      TranslateMessage(ref message);
      DispatchMessage(ref message);
    }
  }

  private static nint WindowProc(nint window, uint message, nint wParam, nint lParam) => message switch
  {
    WM_NCHITTEST => HTTRANSPARENT,
    WM_MOUSEACTIVATE => MA_NOACTIVATE,
    ToggleMessage => Show(window, wParam != 0) ? 1 : 0,
    WM_CLOSE => Call(() => DestroyWindow(window)),
    WM_DESTROY => Call(() => PostQuitMessage(0)),
    _ => DefWindowProc(window, message, wParam, lParam),
  };

  private static nint Call(Action action)
  {
    action();
    return 0;
  }

  // Shows the window over every display, once the exclusion from this process's own capture is confirmed, so it
  // never shows without that guarantee; or hides it. The window's z-order never changes: it was made topmost at
  // creation.
  private static bool Show(nint window, bool active)
  {
    if (!active)
    {
      ShowWindow(window, SW_HIDE);
      SetWindowDisplayAffinity(window, WDA_NONE);
      return false;
    }

    var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
    var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
    var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    SetWindowPos(window, 0, left, top, width, height, SWP_NOZORDER | SWP_SHOWWINDOW);

    if (!SetWindowDisplayAffinity(window, WDA_EXCLUDEFROMCAPTURE))
    {
      _lastShowError = Marshal.GetLastWin32Error();
      ShowWindow(window, SW_HIDE);
      return false;
    }

    return true;
  }

  private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct WNDCLASSEX
  {
    public int cbSize;
    public uint style;
    public WndProc lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public string? lpszMenuName;
    public string lpszClassName;
    public nint hIconSm;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct MSG
  {
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public int ptX;
    public int ptY;
  }

  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern ushort RegisterClassEx(ref WNDCLASSEX wndClass);

  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

  [DllImport("user32.dll")]
  private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

  [DllImport("user32.dll")]
  private static extern bool DestroyWindow(nint window);

  [DllImport("user32.dll")]
  private static extern bool ShowWindow(nint window, int command);

  [DllImport("user32.dll")]
  private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

  [DllImport("user32.dll")]
  private static extern void PostQuitMessage(int exitCode);

  [DllImport("user32.dll")]
  private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

  [DllImport("user32.dll")]
  private static extern bool GetMessage(out MSG message, nint window, uint filterMin, uint filterMax);

  [DllImport("user32.dll")]
  private static extern bool TranslateMessage(ref MSG message);

  [DllImport("user32.dll")]
  private static extern nint DispatchMessage(ref MSG message);

  [DllImport("user32.dll")]
  private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

  [DllImport("user32.dll", SetLastError = true)]
  private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);

  [DllImport("user32.dll")]
  private static extern int GetSystemMetrics(int index);

  [DllImport("gdi32.dll")]
  private static extern nint CreateSolidBrush(uint color);
}
