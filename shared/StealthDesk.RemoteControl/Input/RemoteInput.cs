using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.RemoteControl;

namespace StealthDesk.RemoteControl.Input;

/// <summary>
/// Puts the viewer's mouse and keyboard onto the device, within what the viewer is allowed. A viewer without
/// <see cref="ViewerPermissions.Interact"/> moves nothing; Ctrl+Alt+Del needs
/// <see cref="ViewerPermissions.SendCtrlAltDel"/>. Mouse points come as fractions of the shown display and are turned
/// into pixels on it; a key is typed as text when it is a plain character with no shortcut held, so the device's
/// layout produces it, and otherwise sent as the key itself.
/// </summary>
public sealed class RemoteInput(IInputInjector injector, Func<DisplayInfo?> shownDisplay, ViewerPermissions permissions, ILogger logger)
{
  public void MovePointer(MovePointer message)
  {
    if (At(message.X, message.Y) is { } point)
    {
      injector.MovePointer(point.X, point.Y);
    }
  }

  public void MouseButton(MouseButton message)
  {
    if (At(message.X, message.Y) is { } point)
    {
      injector.MouseButton(point.X, point.Y, message.Button, message.IsPressed);
    }
  }

  public void MouseWheel(MouseWheel message)
  {
    if (At(message.X, message.Y) is { } point)
    {
      injector.MouseWheel(point.X, point.Y, message.DeltaY, message.DeltaX);
    }
  }

  public void Key(KeyEvent message)
  {
    if (!Allowed())
    {
      return;
    }

    if (IsTypable(message.Key, message.Modifiers))
    {
      injector.TypeCharacter(message.Key[0], message.IsPressed);
      return;
    }

    if (KeyMap.Resolve(message.Key, message.Code) is { } resolved)
    {
      injector.Key(resolved.Key, message.IsPressed, resolved.Extended);
      return;
    }

    logger.LogDebug("No virtual key for key '{Key}' (code '{Code}').", message.Key, message.Code);
  }

  public void TypeText(TypeText message)
  {
    if (!Allowed())
    {
      return;
    }

    foreach (var character in message.Text)
    {
      injector.TypeCharacter(character, isPressed: true);
      injector.TypeCharacter(character, isPressed: false);
    }
  }

  public void ResetKeyboard()
  {
    if (Allowed())
    {
      injector.ResetKeys();
    }
  }

  public void CtrlAltDel()
  {
    if (!permissions.SendCtrlAltDel)
    {
      logger.LogDebug("A viewer without the Ctrl+Alt+Del permission asked to send it.");
      return;
    }

    injector.CtrlAltDel();
  }

  // A plain character the device's own layout should produce, as opposed to a named or shortcut key.
  private static bool IsTypable(string key, KeyModifiers modifiers) =>
    key.Length == 1 && !char.IsControl(key[0]) && !modifiers.HasShortcut;

  private (int X, int Y)? At(double x, double y)
  {
    if (!Allowed())
    {
      return null;
    }

    if (shownDisplay() is not { } display)
    {
      logger.LogDebug("Mouse input arrived before a display was shown.");
      return null;
    }

    return PointerMath.ToPixel(display, x, y);
  }

  private bool Allowed()
  {
    if (permissions.Interact)
    {
      return true;
    }

    logger.LogDebug("A view-only viewer sent input, which was dropped.");
    return false;
  }
}
