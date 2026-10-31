using StealthDesk.RemoteControl.Input;

namespace StealthDesk.Agent.Tests.Fakes;

/// <summary>An input injector that does nothing, for sessions whose input isn't what a test looks at.</summary>
internal sealed class NoInput : IInputInjector
{
  public void MovePointer(int x, int y) { }

  public void MouseButton(int x, int y, int button, bool isPressed) { }

  public void MouseWheel(int x, int y, int deltaY, int deltaX) { }

  public void Key(VirtualKey key, bool isPressed, bool extended) { }

  public void TypeCharacter(char character, bool isPressed) { }

  public void ResetKeys() { }

  public void CtrlAltDel() { }
}
