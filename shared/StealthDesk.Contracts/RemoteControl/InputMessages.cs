using MessagePack;

namespace StealthDesk.Contracts.RemoteControl;

/// <summary>
/// Where a mouse message points, as a fraction of the shown display: 0 is its left or top edge, 1 its right or bottom.
/// The desktop client turns it into a pixel on that display, so the viewer needn't know the device's resolution.
/// </summary>
/// <param name="X">Across, 0 at the left edge, 1 at the right.</param>
/// <param name="Y">Down, 0 at the top edge, 1 at the bottom.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record MovePointer(double X, double Y);

/// <summary>A mouse button going down or up, at a point of the shown display.</summary>
/// <param name="Button">0 left, 1 middle, 2 right, 3 and 4 the side buttons.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record MouseButton(int Button, bool IsPressed, double X, double Y);

/// <summary>The wheel turning, at a point of the shown display. One notch is 120.</summary>
/// <param name="DeltaY">Up is positive; down negative.</param>
/// <param name="DeltaX">Right is positive; left negative.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record MouseWheel(double X, double Y, int DeltaY, int DeltaX);

/// <summary>Which modifier keys are held while a <see cref="KeyEvent"/> happens.</summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record KeyModifiers(bool Control, bool Shift, bool Alt, bool Meta)
{
  public static KeyModifiers None { get; } = new(false, false, false, false);

  /// <summary>A shortcut's modifier is held, so the key is a command, not text to type.</summary>
  public bool HasShortcut => Control || Alt || Meta;
}

/// <summary>
/// A key going down or up, named as the browser's keyboard event names it: <paramref name="Key"/> is the character or
/// name it produces (layout and shift applied), <paramref name="Code"/> the physical key's position. The desktop
/// client prefers the code, so the device's own layout decides, and falls back to the key.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record KeyEvent(string Key, string Code, bool IsPressed, KeyModifiers Modifiers);

/// <summary>Text typed as it is, character by character, whatever the device's layout.</summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TypeText(string Text);

/// <summary>Release every key the viewer left held down, e.g. when the remote desktop loses focus.</summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record ResetKeyboard;

/// <summary>Send Ctrl+Alt+Del to the device's session.</summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record CtrlAltDel;

/// <summary>
/// Block or unblock the device's own mouse and keyboard, from the viewer, and the desktop client's answer with the
/// blocking's actual state.
/// </summary>
/// <param name="Active">Block the device's own mouse and keyboard while true.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record BlockInput(bool Active);

/// <summary>
/// Show or hide an opaque overlay covering every one of the device's displays, from the viewer, and the desktop
/// client's answer with the overlay's actual state. The viewer's own screen capture keeps showing the real desktop
/// underneath.
/// </summary>
/// <param name="Active">Show the overlay while true.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record PrivacyScreen(bool Active);

/// <summary>
/// Clipboard text, sent to set the device's clipboard or returned as the device's clipboard. Text longer than
/// <see cref="Clipboard.MaxTextLength"/> is cut, so one side can't flood the other.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record ClipboardText(string Text);

/// <summary>Ask the device for its clipboard text, which comes back as a <see cref="ClipboardText"/>.</summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record RequestClipboard;

/// <summary>
/// The device moved to, or away from, a secure screen (a UAC prompt, the lock screen, or Ctrl+Alt+Del), which a
/// viewer without the elevated-desktop permission can't see. The viewer shows a message while <paramref name="Active"/>.
/// </summary>
/// <param name="Active">A secure screen is shown now.</param>
/// <param name="Screen">The desktop's name, for the message; null when it went back to the normal desktop.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record SecureDesktop(bool Active, string? Screen);

/// <summary>Limits shared by both ends for the clipboard.</summary>
public static class Clipboard
{
  /// <summary>The most clipboard text carried in one direction; longer text is cut to this.</summary>
  public const int MaxTextLength = 1024 * 1024;

  /// <summary>Text no longer than the limit.</summary>
  public static string Limit(string text) => text.Length > MaxTextLength ? text[..MaxTextLength] : text;
}
