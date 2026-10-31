namespace StealthDesk.RemoteControl.Input;

/// <summary>
/// Puts input onto the device's desktop. Mouse points are pixels of the desktop; keys are already resolved to virtual
/// keys and characters, so a platform's injector only turns them into its own system calls.
/// </summary>
public interface IInputInjector
{
  /// <summary>Moves the pointer to a pixel of the desktop.</summary>
  void MovePointer(int x, int y);

  /// <summary>A mouse button down or up, after moving the pointer to the pixel.</summary>
  void MouseButton(int x, int y, int button, bool isPressed);

  /// <summary>The wheel turning at a pixel; one notch is 120.</summary>
  void MouseWheel(int x, int y, int deltaY, int deltaX);

  /// <summary>A virtual key down or up, with the extended-key flag its scan code needs.</summary>
  void Key(VirtualKey key, bool isPressed, bool extended);

  /// <summary>A character typed as a Unicode key, so the device's layout needn't produce it.</summary>
  void TypeCharacter(char character, bool isPressed);

  /// <summary>Releases every key held down.</summary>
  void ResetKeys();

  /// <summary>Sends Ctrl+Alt+Del to this session.</summary>
  void CtrlAltDel();
}
