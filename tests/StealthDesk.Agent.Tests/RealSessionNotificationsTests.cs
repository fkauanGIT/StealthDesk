using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.DesktopClient;

namespace StealthDesk.Agent.Tests;

/// <summary>
/// The real session watcher on this machine: its window actually exists and actually reacts to a
/// <c>WM_WTSSESSION_CHANGE</c> message, the same way Windows itself would deliver a lock, unlock, logon or logoff.
/// Locking or signing out this machine for real isn't something a test may do, so the message is sent by hand.
/// </summary>
[SupportedOSPlatform("windows6.1")]
public sealed class RealSessionNotificationsTests : IDisposable
{
  private const string ClassName = "StealthDeskSessionNotifications";
  private const uint WM_WTSSESSION_CHANGE = 0x02B1;
  private const int WTS_SESSION_LOCK = 7;
  private const int WTS_SESSION_REMOTE_CONTROL = 9;

  private readonly WindowsSessionNotifications _notifications = new(NullLogger<WindowsSessionNotifications>.Instance);

  [Fact]
  public async Task AKnownReason_ArrivesAsItsKind_AndAnUnrelatedOneIsIgnored()
  {
    var window = FindWindowW(ClassName, null);
    Assert.NotEqual(0, window);
    var changes = _notifications.Changes.GetAsyncEnumerator(TestContext.Current.CancellationToken);

    PostMessage(window, WM_WTSSESSION_CHANGE, WTS_SESSION_REMOTE_CONTROL, 0);
    PostMessage(window, WM_WTSSESSION_CHANGE, WTS_SESSION_LOCK, 0);

    Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    Assert.Equal(SessionChangeKind.Locked, changes.Current);
  }

  public void Dispose() => _notifications.Dispose();

  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern nint FindWindowW(string className, string? windowName);

  [DllImport("user32.dll")]
  private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
