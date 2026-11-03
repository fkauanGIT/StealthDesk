using StealthDesk.Contracts.RemoteControl;

namespace StealthDesk.RemoteControl.Input;

/// <summary>
/// The Windows desktop that currently has input (the normal desktop, or a secure one such as a UAC prompt, the lock
/// screen or Ctrl+Alt+Del). A platform implements it; it is faked in tests.
/// </summary>
public interface IInputDesktop
{
    /// <summary>The input desktop's name now (e.g. <c>Default</c> or <c>Winlogon</c>), or null when it can't be read.</summary>
    string? Name();

    /// <summary>Follows the input desktop, so capture and input act on it; false when it couldn't.</summary>
    bool SwitchToInput();
}

/// <summary>What to do about the input desktop after a look at it.</summary>
/// <param name="Follow">Follow the input desktop (the viewer may use secure screens).</param>
/// <param name="Notify">A message for the viewer, or null when nothing changed.</param>
public readonly record struct DesktopDecision(bool Follow, SecureDesktop? Notify);

/// <summary>
/// Decides, each time the input desktop is looked at, whether to follow it onto a secure screen and whether to tell
/// the viewer. A viewer allowed to use secure screens follows them and sees them; one who isn't is told a secure
/// screen is up, and told again when it goes away, and never follows.
/// </summary>
public sealed class SecureDesktopTracker
{
    private const string Normal = "Default";

    private bool _notifiedActive;

    public DesktopDecision Poll(string? inputDesktop, bool canUseSecureScreens)
    {
        var secure = inputDesktop is { Length: > 0 } name && !name.Equals(Normal, StringComparison.OrdinalIgnoreCase);

        if (canUseSecureScreens)
        {
            // The viewer sees everything; follow a secure desktop so capture and input reach it.
            return new DesktopDecision(Follow: secure, Notify: null);
        }

        if (secure == _notifiedActive)
        {
            return new DesktopDecision(Follow: false, Notify: null);
        }

        _notifiedActive = secure;
        return new DesktopDecision(Follow: false, Notify: new SecureDesktop(secure, secure ? inputDesktop : null));
    }
}
