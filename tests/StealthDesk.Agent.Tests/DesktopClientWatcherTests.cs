using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StealthDesk.Agent.Core.DesktopClients;
using StealthDesk.Agent.Tests.Fakes;

namespace StealthDesk.Agent.Tests;

public sealed class DesktopClientWatcherTests
{
  private static readonly DesktopSession Console = new(1, null, IsConsole: true);
  private static readonly DesktopSession Remote = new(3, "bob", IsConsole: false);

  private readonly FakeDesktopSessions _sessions = new();
  private readonly FakeDesktopClientLauncher _launcher = new();
  private readonly FakeTimeProvider _clock = new();

  [Fact]
  public void EverySession_GetsOneClient_TheSignInScreenToo()
  {
    _sessions.Sessions.AddRange([Console, Remote]);
    var watcher = Create();

    watcher.Check();
    watcher.Check();

    Assert.Equal([1, 3], _launcher.Starts);
    Assert.Equal(2, _launcher.Alive.Count());
  }

  [Fact]
  public void AClientThatKeepsExiting_IsStartedAgain_WaitingLongerEachTime()
  {
    _sessions.Sessions.Add(Console);
    var watcher = Create();
    watcher.Check();

    // Each exit waits twice as long as the one before: 2 s, then 4 s, then 8 s.
    foreach (var wait in new[] { 2, 4, 8 })
    {
      var starts = _launcher.Starts.Count;
      _launcher.Clients[^1].Exit();
      watcher.Check();
      _clock.Advance(TimeSpan.FromSeconds(wait) - TimeSpan.FromMilliseconds(100));
      watcher.Check();
      Assert.Equal(starts, _launcher.Starts.Count);

      _clock.Advance(TimeSpan.FromMilliseconds(100));
      watcher.Check();
      Assert.Equal(starts + 1, _launcher.Starts.Count);
    }
  }

  [Fact]
  public void AClientThatStayedUp_IsStartedAgainSoon_WhenItExits()
  {
    _sessions.Sessions.Add(Console);
    var watcher = Create();
    watcher.Check();
    for (var i = 0; i < 3; i++)
    {
      _launcher.Clients[^1].Exit();
      watcher.Check();
      _clock.Advance(TimeSpan.FromMinutes(1));
      watcher.Check();
    }

    // Up for a minute, and gone by the next check.
    _clock.Advance(DesktopClientWatcher.HealthyAfter);
    _launcher.Clients[^1].Exit();
    watcher.Check();
    _clock.Advance(TimeSpan.FromSeconds(2));
    watcher.Check();

    Assert.Equal(5, _launcher.Starts.Count);
  }

  [Fact]
  public void ASessionThatEnds_HasItsClientStopped_AndNoNewOne()
  {
    _sessions.Sessions.AddRange([Console, Remote]);
    var watcher = Create();
    watcher.Check();

    _sessions.Sessions.Remove(Remote);
    watcher.Check();
    _clock.Advance(TimeSpan.FromMinutes(5));
    watcher.Check();

    var remote = Assert.Single(_launcher.Clients, x => x.SessionId == Remote.Id);
    Assert.True(remote.Stopped);
    Assert.Equal([1, 3], _launcher.Starts);
  }

  [Fact]
  public void AClientTheAgentDidntStart_IsStopped_SoEachSessionHasOne()
  {
    _sessions.Sessions.Add(Console);
    var leftOver = _launcher.Add(Console.Id);
    var watcher = Create();

    watcher.Check();
    var second = _launcher.Add(Console.Id);
    watcher.Check();

    Assert.True(leftOver.Stopped);
    Assert.True(second.Stopped);
    var own = Assert.Single(_launcher.Alive);
    Assert.Equal(Console.Id, own.SessionId);
  }

  [Fact]
  public void AClientThatCantStart_IsTriedAgainAfterAWait()
  {
    _sessions.Sessions.Add(Console);
    _launcher.FailingStarts = 1;
    var watcher = Create();

    watcher.Check();
    watcher.Check();
    Assert.Empty(_launcher.Starts);

    _clock.Advance(TimeSpan.FromSeconds(2));
    watcher.Check();
    Assert.Equal([1], _launcher.Starts);
  }

  [Fact]
  public void WithoutTheDesktopClientInstalled_NothingStarts_UntilItIs()
  {
    _sessions.Sessions.Add(Console);
    _launcher.MissingReason = "not there";
    var watcher = Create();

    watcher.Check();
    Assert.Empty(_launcher.Starts);

    _launcher.MissingReason = null;
    watcher.Check();
    Assert.Equal([1], _launcher.Starts);
  }

  [Fact]
  public async Task StoppingTheAgent_StopsItsClients()
  {
    _sessions.Sessions.AddRange([Console, Remote]);
    using var watcher = Create();

    await watcher.StartAsync(TestContext.Current.CancellationToken);
    Assert.True(await Wait.UntilAsync(() => _launcher.Starts.Count == 2), "The clients weren't started.");
    await watcher.StopAsync(TestContext.Current.CancellationToken);

    Assert.All(_launcher.Clients, x => Assert.True(x.Stopped));
  }

  private DesktopClientWatcher Create() => new(_sessions, _launcher, _clock, NullLogger<DesktopClientWatcher>.Instance);
}
