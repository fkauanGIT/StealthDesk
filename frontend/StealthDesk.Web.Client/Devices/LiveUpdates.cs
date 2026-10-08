using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Realtime;

namespace StealthDesk.Web.Client.Devices;

public enum LiveState
{
  Connecting,
  Live,
  Reconnecting,
}

/// <summary>
/// Keeps the <see cref="DeviceStore"/> current with the changes the server pushes, for the devices it already knows:
/// a device that appears later shows up on the next load.
/// </summary>
public interface ILiveUpdates
{
  LiveState State { get; }

  event Action? StateChanged;

  /// <summary>Connects and keeps trying until it does; calling it again does nothing.</summary>
  Task StartAsync();

  /// <summary>Closes the connection, e.g. on sign-out, so the next start connects as whoever signs in.</summary>
  Task StopAsync();
}

public sealed class LiveUpdates(
  DeviceStore store,
  Uri endpoint,
  ILogger<LiveUpdates> logger,
  Action<HttpConnectionOptions>? configure = null) : ILiveUpdates, IAsyncDisposable
{
  private static readonly TimeSpan[] _retryDelays =
    [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

  private readonly SemaphoreSlim _syncing = new(1, 1);
  private CancellationTokenSource _stopping = new();
  private HubConnection? _connection;
  private HashSet<Guid> _subscribed = [];

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
      // A new connection is in no group: everything is subscribed again.
      _subscribed = [];
      await SetState(LiveState.Live);
      // Changes sent while the connection was down never arrived.
      await store.LoadAsync();
      await SyncSubscriptionsAsync();
    };
    store.Changed += OnStoreChanged;

    // Automatic reconnection only covers a connection that was open once; the first one is retried here.
    for (var attempt = 0; !_stopping.IsCancellationRequested; attempt++)
    {
      try
      {
        await Task.Delay(_retryDelays[Math.Min(attempt, _retryDelays.Length - 1)], _stopping.Token);
        await _connection.StartAsync(_stopping.Token);
        await SetState(LiveState.Live);
        await SyncSubscriptionsAsync();
        return;
      }
      catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
      {
        return;
      }
      catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
      {
        // Retrying can't help without a session; signing in again starts the connection anew.
        await StopAsync();
        store.ReportSessionExpired();
        return;
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Could not connect to live updates; retrying.");
      }
    }
  }

  public async Task StopAsync()
  {
    await _stopping.CancelAsync();
    store.Changed -= OnStoreChanged;
    var connection = _connection;
    _connection = null;
    _subscribed = [];
    _stopping = new CancellationTokenSource();
    await SetState(LiveState.Connecting);
    if (connection is not null)
    {
      await connection.DisposeAsync();
    }
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync();
    _stopping.Dispose();
    _syncing.Dispose();
  }

  private void OnStoreChanged() => _ = SyncSubscriptionsAsync();

  // Makes the server's groups match the devices the store knows. Ids the server left out (not readable) count as
  // subscribed too, so they aren't asked for again on every change.
  private async Task SyncSubscriptionsAsync()
  {
    await _syncing.WaitAsync();
    try
    {
      if (_connection is not { State: HubConnectionState.Connected } connection)
      {
        return;
      }

      var wanted = store.KnownIds.ToHashSet();
      foreach (var batch in wanted.Except(_subscribed).Chunk(IDashboardHub.MaxDevicesPerSubscription))
      {
        var reply = await connection.InvokeAsync<GatewayReply<IReadOnlyList<Guid>>>(
          nameof(IDashboardHub.SubscribeToDevices), batch, _stopping.Token);
        if (!reply.Accepted)
        {
          logger.LogWarning("The server refused a live update subscription: {Error}", reply.Error);
          continue;
        }

        _subscribed.UnionWith(batch);
      }

      var gone = _subscribed.Except(wanted).ToArray();
      if (gone.Length > 0)
      {
        await connection.InvokeAsync(nameof(IDashboardHub.UnsubscribeFromDevices), gone, _stopping.Token);
        _subscribed.ExceptWith(gone);
      }
    }
    catch (Exception ex)
    {
      // Nobody awaits this. The next change or reconnection tries again; while stopping there is nothing to say.
      if (!_stopping.IsCancellationRequested)
      {
        logger.LogWarning(ex, "Could not update the live update subscriptions.");
      }
    }
    finally
    {
      _syncing.Release();
    }
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
