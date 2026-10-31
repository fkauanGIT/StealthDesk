using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using StealthDesk.RemoteControl.Input;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace StealthDesk.DesktopClient;

/// <summary>
/// Puts input onto the session's desktop with <c>SendInput</c>. Mouse points come as desktop pixels and go out as a
/// fraction of the whole virtual screen, which is what an absolute mouse move wants. Calls are serialized so a press
/// and its release never cross.
/// </summary>
[SupportedOSPlatform("windows6.1")]
internal sealed unsafe class WindowsInput(ILogger<WindowsInput> logger) : IInputInjector
{
  // mouseData for the side buttons.
  private const uint XButton1 = 0x0001;
  private const uint XButton2 = 0x0002;

  private readonly Lock _gate = new();

  public void MovePointer(int x, int y)
  {
    lock (_gate)
    {
      Send(MouseMove(x, y, MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE));
    }
  }

  public void MouseButton(int x, int y, int button, bool isPressed)
  {
    var (flags, data) = button switch
    {
      0 => (isPressed ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN : MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP, 0u),
      1 => (isPressed ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEDOWN : MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEUP, 0u),
      2 => (isPressed ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN : MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP, 0u),
      3 => (isPressed ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_XDOWN : MOUSE_EVENT_FLAGS.MOUSEEVENTF_XUP, XButton1),
      4 => (isPressed ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_XDOWN : MOUSE_EVENT_FLAGS.MOUSEEVENTF_XUP, XButton2),
      _ => ((MOUSE_EVENT_FLAGS)0, 0u),
    };

    if (flags == 0)
    {
      return;
    }

    lock (_gate)
    {
      // Move first so the button lands where the viewer clicked.
      Send(MouseMove(x, y, MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE));
      Send(MouseAt(flags, data));
    }
  }

  public void MouseWheel(int x, int y, int deltaY, int deltaX)
  {
    lock (_gate)
    {
      if (deltaY != 0)
      {
        Send(MouseMove(x, y, MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL, (uint)deltaY));
      }

      if (deltaX != 0)
      {
        Send(MouseMove(x, y, MOUSE_EVENT_FLAGS.MOUSEEVENTF_HWHEEL, (uint)deltaX));
      }
    }
  }

  public void Key(VirtualKey key, bool isPressed, bool extended)
  {
    lock (_gate)
    {
      Send(KeyboardInput(new KEYBDINPUT
      {
        wVk = (VIRTUAL_KEY)key,
        dwFlags = (isPressed ? 0 : KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP)
          | (extended ? KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY : 0),
      }));
    }
  }

  public void TypeCharacter(char character, bool isPressed)
  {
    lock (_gate)
    {
      Send(KeyboardInput(new KEYBDINPUT
      {
        wScan = character,
        dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE | (isPressed ? 0 : KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP),
      }));
    }
  }

  public void ResetKeys()
  {
    lock (_gate)
    {
      foreach (var key in Enum.GetValues<VirtualKey>())
      {
        // Toggles (Caps/Num/Scroll lock) report their lit state here, not a held key: leave them.
        if (key is VirtualKey.Capital or VirtualKey.NumLock or VirtualKey.ScrollLock)
        {
          continue;
        }

        if ((PInvoke.GetAsyncKeyState((int)key) & 0x8000) != 0)
        {
          Send(KeyboardInput(new KEYBDINPUT { wVk = (VIRTUAL_KEY)key, dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP }));
        }
      }
    }
  }

  public void CtrlAltDel()
  {
    try
    {
      using var identity = WindowsIdentity.GetCurrent();
      // A service (LocalSystem) generates the sequence directly; anything else asks for it as the user.
      PInvoke.SendSAS(AsUser: !identity.IsSystem);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Ctrl+Alt+Del couldn't be sent.");
    }
  }

  private static INPUT MouseAt(MOUSE_EVENT_FLAGS flags, uint data) => new()
  {
    type = INPUT_TYPE.INPUT_MOUSE,
    Anonymous = { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data, dwExtraInfo = (nuint)PInvoke.GetMessageExtraInfo().Value } },
  };

  private static INPUT MouseMove(int x, int y, MOUSE_EVENT_FLAGS flags, uint data = 0)
  {
    // The whole virtual screen maps to 0..65535 for an absolute move.
    var left = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
    var top = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
    var width = Math.Max(1, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN));
    var height = Math.Max(1, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));
    return new INPUT
    {
      type = INPUT_TYPE.INPUT_MOUSE,
      Anonymous =
      {
        mi = new MOUSEINPUT
        {
          dx = (x - left) * 65535 / width,
          dy = (y - top) * 65535 / height,
          mouseData = data,
          dwFlags = flags | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK,
          dwExtraInfo = (nuint)PInvoke.GetMessageExtraInfo().Value,
        },
      },
    };
  }

  private static INPUT KeyboardInput(KEYBDINPUT keyboard)
  {
    keyboard.dwExtraInfo = (nuint)PInvoke.GetMessageExtraInfo().Value;
    return new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD, Anonymous = { ki = keyboard } };
  }

  private void Send(INPUT input)
  {
    Span<INPUT> inputs = [input];
    if (PInvoke.SendInput(inputs, sizeof(INPUT)) == 0)
    {
      logger.LogDebug("SendInput sent nothing for a {Type} event.", input.type);
    }
  }
}
