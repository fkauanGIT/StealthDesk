using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl;

namespace StealthDesk.DesktopClient;

/// <summary>
/// A hidden window registered for <c>WM_WTSSESSION_CHANGE</c>, so the session's lock, unlock, logon and logoff reach
/// this process as plain window messages. One instance watches for as long as a remote control session is open.
/// </summary>
[SupportedOSPlatform("windows6.1")]
internal sealed class WindowsSessionNotifications : ISessionNotifications
{
  private const uint WM_WTSSESSION_CHANGE = 0x02B1;
  private const uint WM_CLOSE = 0x0010;
  private const uint WM_DESTROY = 0x0002;
  private const uint NOTIFY_FOR_THIS_SESSION = 0;

  private const string ClassName = "StealthDeskSessionNotifications";

  // Kept alive for the process's lifetime: native code calls back into it as long as the window class exists.
  private static readonly WndProc Procedure = WindowProc;

  private readonly ILogger<WindowsSessionNotifications> _logger;
  private readonly Channel<SessionChangeKind> _changes = Channel.CreateUnbounded<SessionChangeKind>();
  private nint _window;

  public WindowsSessionNotifications(ILogger<WindowsSessionNotifications> logger)
  {
    _logger = logger;
    Start();
  }

  public IAsyncEnumerable<SessionChangeKind> Changes => _changes.Reader.ReadAllAsync();

  public void Dispose()
  {
    if (_window != 0)
    {
      PostMessage(_window, WM_CLOSE, 0, 0);
      _window = 0;
    }

    _changes.Writer.TryComplete();
  }

  // A window may only be touched by the thread that created it, so it gets a dedicated thread with its own message
  // loop, for as long as this instance watches.
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
        lpfnWndProc = Procedure,
        lpszClassName = ClassName,
      };
      if (RegisterClassEx(ref wndClass) == 0)
      {
        _logger.LogWarning("The session watcher's window class couldn't be registered (Windows error {Error}).", Marshal.GetLastWin32Error());
        return;
      }

      _window = CreateWindowEx(0, ClassName, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, 0);
      if (_window == 0)
      {
        _logger.LogWarning("The session watcher's window couldn't be created (Windows error {Error}).", Marshal.GetLastWin32Error());
        return;
      }

      if (!WTSRegisterSessionNotification(_window, NOTIFY_FOR_THIS_SESSION))
      {
        _logger.LogWarning("The session watcher couldn't register for session notifications (Windows error {Error}).", Marshal.GetLastWin32Error());
        DestroyWindow(_window);
        _window = 0;
        return;
      }

      _instances[_window] = this;
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

  // The native callback has no way to carry `this`, so each live window is looked up by its handle.
  private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, WindowsSessionNotifications> _instances = new();

  private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
  {
    switch (message)
    {
      case WM_WTSSESSION_CHANGE:
        if (_instances.TryGetValue(window, out var instance) && SessionChangeReasons.From((int)wParam) is { } kind)
        {
          instance._changes.Writer.TryWrite(kind);
        }

        return 0;
      case WM_CLOSE:
        WTSUnRegisterSessionNotification(window);
        _instances.TryRemove(window, out _);
        DestroyWindow(window);
        return 0;
      case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
      default:
        return DefWindowProc(window, message, wParam, lParam);
    }
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
  private static extern void PostQuitMessage(int exitCode);

  [DllImport("user32.dll")]
  private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

  [DllImport("user32.dll")]
  private static extern bool GetMessage(out MSG message, nint window, uint filterMin, uint filterMax);

  [DllImport("user32.dll")]
  private static extern bool TranslateMessage(ref MSG message);

  [DllImport("user32.dll")]
  private static extern nint DispatchMessage(ref MSG message);

  [DllImport("wtsapi32.dll", SetLastError = true)]
  private static extern bool WTSRegisterSessionNotification(nint window, uint flags);

  [DllImport("wtsapi32.dll", SetLastError = true)]
  private static extern bool WTSUnRegisterSessionNotification(nint window);
}
