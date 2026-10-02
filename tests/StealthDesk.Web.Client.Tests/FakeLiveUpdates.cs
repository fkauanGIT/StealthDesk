using StealthDesk.Web.Client.Devices;

namespace StealthDesk.Web.Client.Tests;

/// <summary>Live updates without a server: the test sets the connection state.</summary>
internal sealed class FakeLiveUpdates : ILiveUpdates
{
  public event Action? StateChanged;

  public LiveState State { get; private set; } = LiveState.Connecting;

  public bool Started { get; private set; }

  public Task StartAsync()
  {
    Started = true;
    return Task.CompletedTask;
  }

  public void Become(LiveState state)
  {
    State = state;
    StateChanged?.Invoke();
  }
}
