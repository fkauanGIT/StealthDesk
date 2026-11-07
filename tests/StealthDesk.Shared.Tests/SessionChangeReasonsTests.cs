using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl;

namespace StealthDesk.Shared.Tests;

public class SessionChangeReasonsTests
{
  [Theory]
  [InlineData(7, SessionChangeKind.Locked)]
  [InlineData(8, SessionChangeKind.Unlocked)]
  [InlineData(6, SessionChangeKind.LoggedOff)]
  [InlineData(5, SessionChangeKind.UserSwitched)]
  public void AKnownReason_MapsToItsKind(int reason, SessionChangeKind expected) =>
    Assert.Equal(expected, SessionChangeReasons.From(reason));

  [Theory]
  [InlineData(1)] // WTS_CONSOLE_CONNECT
  [InlineData(2)] // WTS_CONSOLE_DISCONNECT
  [InlineData(3)] // WTS_REMOTE_CONNECT
  [InlineData(4)] // WTS_REMOTE_DISCONNECT
  [InlineData(9)] // WTS_SESSION_REMOTE_CONTROL
  [InlineData(0)]
  public void AnUnreportedReason_MapsToNothing(int reason) =>
    Assert.Null(SessionChangeReasons.From(reason));
}
