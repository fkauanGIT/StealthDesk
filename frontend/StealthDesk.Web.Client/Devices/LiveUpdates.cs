using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Realtime;

namespace StealthDesk.Web.Client.Devices;

public enum LiveState
{
  Connecting,
  Live,
  Reconnecting,
}

/// <summary>Keeps the <see cref="DeviceStore"/> current with the changes the server pushes.</summary>
public interface ILiveUpdates
{
  LiveState State { get; }

  event Action? StateChanged;

  /// <summary>Connects and keeps trying until it does; calling it again does nothing.</summary>
  Task StartAsync();
}

public sealed class LiveUpdates(
  DeviceStore store,
  Uri endpoint,
  ILogger<LiveUpdates> logger,
  Action<HttpConnectionOptions>? configure = null) : ILiveUpdates, IAsyncDisposable
{
  private static readonly TimeSpan[] _retryDelays =
    [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

  private readonly CancellationTokenSource _stopping = new();
  private HubConnection? _connection;

  public event Action? StateChanged;

  public LiveState State { get; private set; } = LiveState.Connecting;

  public async Task StartAsync()
  {
    if (_connection is not null)
    {
      return;
    }

    _connection = new HubConnectionBuilder()
      .WithUrl(endpoint, options => configure?.Invoke(options))
      .WithAutomaticReconnect(new RetryForever())
      .Build();

    _connection.On<DeviceSummary>(nameof(IDashboardCallbacks.DeviceChanged), store.Apply);
    _connection.Reconnecting += _ => SetState(LiveState.Reconnecting);
    _connection.Reconnected += async _ =>
    {
      await SetState(LiveState.Live);
      // Changes sent while the connection was down never arrived.
      await store.LoadAsync();
    };

    // Automatic reconnection only covers a connection that was open once; the first one is retried here.
    for (var attempt = 0; !_stopping.IsCancellationRequested; attempt++)
    {
      try
      {
        await Task.Delay(_retryDelays[Math.Min(attempt, _retryDelays.Length - 1)], _stopping.Token);
        await _connection.StartAsync(_stopping.Token);
        await SetState(LiveState.Live);
        return;
      }
      catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
      {
        return;
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Could not connect to live updates; retrying.");
      }
    }
  }

  public async ValueTask DisposeAsync()
  {
    await _stopping.CancelAsync();
    if (_connection is not null)
    {
      await _connection.DisposeAsync();
    }

    _stopping.Dispose();
  }

  private Task SetState(LiveState state)
  {
    State = state;
    StateChanged?.Invoke();
    return Task.CompletedTask;
  }

  // A dashboard left open must come back whenever the server does.
  private sealed class RetryForever : IRetryPolicy
  {
    public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
      _retryDelays[(int)Math.Min(retryContext.PreviousRetryCount, _retryDelays.Length - 1)];
  }
}
