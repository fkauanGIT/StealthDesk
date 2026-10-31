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
