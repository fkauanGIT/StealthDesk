using StealthDesk.Contracts.RemoteControl;

namespace StealthDesk.RemoteControl;

/// <summary>Watches the device's own Windows session for it locking, unlocking, switching user, or signing out.</summary>
public interface ISessionNotifications : IDisposable
{
  /// <summary>Changes as they happen, until disposed.</summary>
  IAsyncEnumerable<SessionChangeKind> Changes { get; }
}

/// <summary>What a platform's own session-change reason means, kept apart from the native API so it can be tested without one.</summary>
public static class SessionChangeReasons
{
  // From WTS_SESSION_* in wtsapi32.h: the subset Scope asks for; console/remote connect and disconnect aren't.
  private const int WtsSessionLogon = 5;
  private const int WtsSessionLogoff = 6;
  private const int WtsSessionLock = 7;
  private const int WtsSessionUnlock = 8;

  /// <summary>The kind a native reason code means, or null when it isn't one this feature reports.</summary>
  public static SessionChangeKind? From(int reason) => reason switch
  {
    WtsSessionLock => SessionChangeKind.Locked,
    WtsSessionUnlock => SessionChangeKind.Unlocked,
    WtsSessionLogoff => SessionChangeKind.LoggedOff,
    // A logon while this session's client is already running means another user just took it over, not its own start.
    WtsSessionLogon => SessionChangeKind.UserSwitched,
    _ => null,
  };
}
