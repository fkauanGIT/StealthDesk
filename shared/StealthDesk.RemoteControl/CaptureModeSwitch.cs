using StealthDesk.Contracts.RemoteControl;

namespace StealthDesk.RemoteControl;

/// <summary>
/// Which of the two ways a session shows the screen is active now: regions, until the viewer switches it. Read by
/// both streamers on every loop, so a read or write never needs a lock.
/// </summary>
public sealed class CaptureModeSwitch
{
  private volatile int _mode;

  public CaptureMode Mode
  {
    get => (CaptureMode)_mode;
    set => _mode = (int)value;
  }
}
