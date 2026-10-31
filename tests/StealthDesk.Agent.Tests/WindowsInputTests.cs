using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.DesktopClient;
using StealthDesk.RemoteControl.Input;

namespace StealthDesk.Agent.Tests;

/// <summary>
/// The Windows injector reaches a real window: text typed and a key pressed through <see cref="WindowsInput"/> land in
/// an edit control on the interactive desktop. Needs that desktop, so it is skipped where there is none.
/// </summary>
[SupportedOSPlatform("windows6.1")]
public sealed class WindowsInputTests
{
  private const uint WS_POPUP = 0x80000000;
  private const uint WS_VISIBLE = 0x10000000;
  private const uint WS_BORDER = 0x00800000;
  private const int WM_GETTEXT = 0x000D;
  private const int WM_GETTEXTLENGTH = 0x000E;

  [Fact]
  public void TypedText_ReachesAnEditControl()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses Windows input APIs.");

    Run(input =>
    {
      foreach (var character in "hello")
      {
        input.TypeCharacter(character, isPressed: true);
        input.TypeCharacter(character, isPressed: false);
      }
    }, out var text);

    Assert.SkipWhen(text is null, "No interactive desktop to show a window on.");
    Assert.Equal("hello", text);
  }

  [Fact]
  public void AKeyPress_ReachesAnEditControl()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses Windows input APIs.");

    Run(input =>
    {
      // The A key, with no shortcut held, types its character into the control.
      input.Key(VirtualKey.A, isPressed: true, extended: false);
      input.Key(VirtualKey.A, isPressed: false, extended: false);
    }, out var text);

    Assert.SkipWhen(text is null, "No interactive desktop to show a window on.");
    Assert.Equal("a", text?.ToLowerInvariant());
  }

  // Runs a window with its own message pump on a dedicated thread, injects, then reads the control's text back.
  private static void Run(Action<WindowsInput> inject, out string? text)
  {
    var ready = new ManualResetEventSlim();
    var stop = new ManualResetEventSlim();
    nint window = 0;
    var focused = false;
    string? read = null;

    var ui = new Thread(() =>
    {
      window = CreateWindowExW(0, "EDIT", string.Empty, WS_POPUP | WS_VISIBLE | WS_BORDER, 100, 100, 400, 200, 0, 0, 0, 0);
      if (window == 0)
      {
        ready.Set();
        return;
      }

      // SendInput reaches whatever owns the foreground, so inject only once this window does. On a busy or headless
      // desktop it may never; the test then skips rather than failing on input that landed elsewhere.
      var deadline = DateTime.UtcNow.AddSeconds(5);
      while (DateTime.UtcNow < deadline && !focused)
      {
        ForceForeground(window);
        SetFocus(window);
        Pump();
        focused = GetForegroundWindow() == window;
        if (!focused)
        {
          Thread.Sleep(100);
        }
      }

      ready.Set();
      while (!stop.IsSet)
      {
        Pump();
        Thread.Sleep(10);
      }

      var length = (int)SendMessageW(window, WM_GETTEXTLENGTH, 0, 0);
      var buffer = new StringBuilder(length + 1);
      SendMessageW(window, WM_GETTEXT, buffer.Capacity, buffer);
      read = buffer.ToString();
      DestroyWindow(window);
    });
    ui.SetApartmentState(ApartmentState.STA);
    ui.IsBackground = true;
    ui.Start();

    ready.Wait(TimeSpan.FromSeconds(10));
    if (window == 0 || !focused)
    {
      text = null;
      stop.Set();
      ui.Join(TimeSpan.FromSeconds(5));
      return;
    }

    inject(new WindowsInput(NullLogger<WindowsInput>.Instance));
    Thread.Sleep(500);
    stop.Set();
    ui.Join(TimeSpan.FromSeconds(5));
    text = read;
  }

  // SetForegroundWindow is refused unless the caller already owns the foreground; attaching to the current foreground
  // thread's input lets the window take it, so injected input reaches it.
  private static void ForceForeground(nint window)
  {
    const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001;
    var foreground = GetForegroundWindow();
    var other = GetWindowThreadProcessId(foreground, out _);
    var mine = GetCurrentThreadId();
    AttachThreadInput(mine, other, true);
    try
    {
      SetWindowPos(window, -1, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE); // HWND_TOPMOST
      SetWindowPos(window, -2, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE); // HWND_NOTOPMOST
      BringWindowToTop(window);
      SetForegroundWindow(window);
    }
    finally
    {
      AttachThreadInput(mine, other, false);
    }
  }

  private static void Pump()
  {
    while (PeekMessageW(out var message, 0, 0, 0, 0x0001))
    {
      TranslateMessage(ref message);
      DispatchMessageW(ref message);
    }
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct MSG
  {
    public nint hwnd;
    public uint message;
    public nuint wParam;
    public nint lParam;
    public uint time;
    public int ptX;
    public int ptY;
  }

  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

  [DllImport("user32.dll")]
  private static extern bool DestroyWindow(nint window);

  [DllImport("user32.dll")]
  private static extern bool SetForegroundWindow(nint window);

  [DllImport("user32.dll")]
  private static extern nint SetFocus(nint window);

  [DllImport("user32.dll")]
  private static extern nint GetForegroundWindow();

  [DllImport("user32.dll")]
  private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

  [DllImport("kernel32.dll")]
  private static extern uint GetCurrentThreadId();

  [DllImport("user32.dll")]
  private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

  [DllImport("user32.dll")]
  private static extern bool BringWindowToTop(nint window);

  [DllImport("user32.dll")]
  private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int cx, int cy, uint flags);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern bool PeekMessageW(out MSG message, nint window, uint filterMin, uint filterMax, uint remove);

  [DllImport("user32.dll")]
  private static extern bool TranslateMessage(ref MSG message);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern nint DispatchMessageW(ref MSG message);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern nint SendMessageW(nint window, int message, int wParam, int lParam);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern nint SendMessageW(nint window, int message, int wParam, StringBuilder text);
}
