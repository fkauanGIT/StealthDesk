namespace StealthDesk.RemoteControl;

/// <summary>
/// An opaque overlay covering every one of the device's displays, for the person in front of it: a platform shows it
/// excluded from this process's own screen capture, so a remote control session's viewer keeps seeing the real
/// desktop underneath. A platform without one always answers false.
/// </summary>
public interface IPrivacyScreen
{
  /// <summary>Shows or hides the overlay; the state that took effect, which may differ from what was asked.</summary>
  bool SetActive(bool active);
}
