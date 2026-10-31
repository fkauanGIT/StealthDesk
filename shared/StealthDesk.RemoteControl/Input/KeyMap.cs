namespace StealthDesk.RemoteControl.Input;

/// <summary>A key resolved to a virtual key, and whether its scan code carries the extended-key prefix.</summary>
public readonly record struct ResolvedKey(VirtualKey Key, bool Extended);

/// <summary>
/// Turns a browser keyboard event into a Windows virtual key. The physical position (<c>code</c>, like
/// <c>"KeyA"</c>) is tried first, so the device's own layout decides what the key means; the produced value
/// (<c>key</c>, like <c>"a"</c> or <c>"Enter"</c>) is the fallback when the position isn't one we know.
/// </summary>
public static class KeyMap
{
  /// <summary>The virtual key for a browser event, or null when neither its code nor its key is one we map.</summary>
  public static ResolvedKey? Resolve(string? key, string? code)
  {
    if (FromCode(code) is { } fromCode)
    {
      return new ResolvedKey(fromCode, IsExtended(fromCode, code));
    }

    return FromKey(key) is { } fromKey ? new ResolvedKey(fromKey, IsExtended(fromKey, code)) : null;
  }

  private static VirtualKey? FromCode(string? code) => code switch
  {
    null or "" => null,
    _ when code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal) && code[3] is >= 'A' and <= 'Z'
      => (VirtualKey)((int)VirtualKey.A + (code[3] - 'A')),
    _ when code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal) && code[5] is >= '0' and <= '9'
      => (VirtualKey)((int)VirtualKey.D0 + (code[5] - '0')),
    _ when code.Length == 7 && code.StartsWith("Numpad", StringComparison.Ordinal) && code[6] is >= '0' and <= '9'
      => (VirtualKey)((int)VirtualKey.Numpad0 + (code[6] - '0')),
    _ when (code.Length == 2 || code.Length == 3) && code[0] == 'F' && ushort.TryParse(code.AsSpan(1), out var n) && n is >= 1 and <= 12
      => (VirtualKey)((int)VirtualKey.F1 + (n - 1)),
    "Backspace" => VirtualKey.Back,
    "Tab" => VirtualKey.Tab,
    "Enter" => VirtualKey.Return,
    "NumpadEnter" => VirtualKey.Return,
    "Escape" => VirtualKey.Escape,
    "Space" => VirtualKey.Space,
    "Minus" => VirtualKey.OemMinus,
    "Equal" => VirtualKey.OemPlus,
    "BracketLeft" => VirtualKey.Oem4,
    "BracketRight" => VirtualKey.Oem6,
    "Backslash" => VirtualKey.Oem5,
    "Semicolon" => VirtualKey.Oem1,
    "Quote" => VirtualKey.Oem7,
    "Backquote" => VirtualKey.Oem3,
    "Comma" => VirtualKey.OemComma,
    "Period" => VirtualKey.OemPeriod,
    "Slash" => VirtualKey.Oem2,
    "CapsLock" => VirtualKey.Capital,
    "NumLock" => VirtualKey.NumLock,
    "ScrollLock" => VirtualKey.ScrollLock,
    "Pause" => VirtualKey.Pause,
    "Insert" => VirtualKey.Insert,
    "Delete" => VirtualKey.Delete,
    "Home" => VirtualKey.Home,
    "End" => VirtualKey.End,
    "PageUp" => VirtualKey.Prior,
    "PageDown" => VirtualKey.Next,
    "ArrowLeft" => VirtualKey.Left,
    "ArrowUp" => VirtualKey.Up,
    "ArrowRight" => VirtualKey.Right,
    "ArrowDown" => VirtualKey.Down,
    "ShiftLeft" => VirtualKey.LeftShift,
    "ShiftRight" => VirtualKey.RightShift,
    "ControlLeft" => VirtualKey.LeftControl,
    "ControlRight" => VirtualKey.RightControl,
    "AltLeft" => VirtualKey.LeftMenu,
    "AltRight" => VirtualKey.RightMenu,
    "MetaLeft" => VirtualKey.LeftWindows,
    "MetaRight" => VirtualKey.RightWindows,
    "ContextMenu" => VirtualKey.Apps,
    "NumpadAdd" => VirtualKey.Add,
    "NumpadSubtract" => VirtualKey.Subtract,
    "NumpadMultiply" => VirtualKey.Multiply,
    "NumpadDivide" => VirtualKey.Divide,
    "NumpadDecimal" => VirtualKey.Decimal,
    _ => null,
  };

  private static VirtualKey? FromKey(string? key) => key switch
  {
    null or "" => null,
    " " => VirtualKey.Space,
    "Enter" => VirtualKey.Return,
    "Tab" => VirtualKey.Tab,
    "Backspace" => VirtualKey.Back,
    "Escape" or "Esc" => VirtualKey.Escape,
    "Delete" or "Del" => VirtualKey.Delete,
    "Insert" => VirtualKey.Insert,
    "Home" => VirtualKey.Home,
    "End" => VirtualKey.End,
    "PageUp" => VirtualKey.Prior,
    "PageDown" => VirtualKey.Next,
    "ArrowLeft" or "Left" => VirtualKey.Left,
    "ArrowUp" or "Up" => VirtualKey.Up,
    "ArrowRight" or "Right" => VirtualKey.Right,
    "ArrowDown" or "Down" => VirtualKey.Down,
    "Shift" => VirtualKey.Shift,
    "Control" => VirtualKey.Control,
    "Alt" => VirtualKey.Menu,
    "Meta" or "OS" => VirtualKey.LeftWindows,
    "ContextMenu" => VirtualKey.Apps,
    "CapsLock" => VirtualKey.Capital,
    "NumLock" => VirtualKey.NumLock,
    "ScrollLock" => VirtualKey.ScrollLock,
    "Pause" => VirtualKey.Pause,
    _ when key.Length == 1 && key[0] is >= 'a' and <= 'z' => (VirtualKey)((int)VirtualKey.A + (key[0] - 'a')),
    _ when key.Length == 1 && key[0] is >= 'A' and <= 'Z' => (VirtualKey)((int)VirtualKey.A + (key[0] - 'A')),
    _ when key.Length == 1 && key[0] is >= '0' and <= '9' => (VirtualKey)((int)VirtualKey.D0 + (key[0] - '0')),
    _ when (key.Length == 2 || key.Length == 3) && key[0] == 'F' && ushort.TryParse(key.AsSpan(1), out var n) && n is >= 1 and <= 12
      => (VirtualKey)((int)VirtualKey.F1 + (n - 1)),
    _ => null,
  };

  // Keys whose scan code needs the E0 prefix so Windows tells them from a numpad key of the same name. The right-hand
  // modifiers and the navigation cluster; Enter only when it is the numpad's.
  private static bool IsExtended(VirtualKey key, string? code) => key switch
  {
    VirtualKey.Return => code == "NumpadEnter",
    VirtualKey.RightControl or VirtualKey.RightMenu
      or VirtualKey.Insert or VirtualKey.Delete or VirtualKey.Home or VirtualKey.End
      or VirtualKey.Prior or VirtualKey.Next
      or VirtualKey.Left or VirtualKey.Up or VirtualKey.Right or VirtualKey.Down
      or VirtualKey.NumLock or VirtualKey.Divide => true,
    _ => false,
  };
}
