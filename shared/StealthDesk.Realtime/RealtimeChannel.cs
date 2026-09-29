using System.Reflection;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StealthDesk.Core;

namespace StealthDesk.Realtime;

internal sealed class RealtimeChannel<TServer, TCallbacks>(
  IServiceProvider services,
  Backoff reconnectBackoff,
  ILogger<RealtimeChannel<TServer, TCallbacks>> logger) : IRealtimeChannel<TServer>
  where TServer : class
  where TCallbacks : class
{
  private readonly SemaphoreSlim _openGate = new(1, 1);
  private HubConnection? _connection;
  private TServer? _server;

  public event Func<Task>? Restored;
  public event Func<Exception?, Task>? Lost;

  public TServer Server => _server ??= ServerProxy<TServer>.Create(
    () => _connection ?? throw new InvalidOperationException("The channel is not open."));

  public HubConnectionState State => _connection?.State ?? HubConnectionState.Disconnected;

  public async Task<bool> OpenAsync(
    Uri endpoint,
    Action<HttpConnectionOptions>? configure = null,
    CancellationToken cancellationToken = default)
  {
    await _openGate.WaitAsync(cancellationToken);
    try
    {
      if (State == HubConnectionState.Connected)
      {
        return true;
      }

      await CloseCurrentConnection();

      var connection = services.GetRequiredService<IHubConnectionBuilder>()
        .WithUrl(endpoint, options => configure?.Invoke(options))
        .WithAutomaticReconnect(new BackoffRetryPolicy(reconnectBackoff))
        .Build();

      connection.Reconnected += _ => Raise(Restored);
      connection.Closed += error => Raise(Lost, error);
      ListenForCallbacks(connection);

      _connection = connection;
      logger.LogInformation("Opening realtime channel to {Endpoint}.", endpoint);
      await connection.StartAsync(cancellationToken);
      logger.LogInformation("Realtime channel open ({ConnectionId}).", connection.ConnectionId);
      return true;
    }
    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
    {
      logger.LogWarning(ex, "Could not open the realtime channel to {Endpoint}.", endpoint);
      return false;
    }
    finally
    {
      _openGate.Release();
    }
  }

  public async ValueTask DisposeAsync()
  {
    await CloseCurrentConnection();
    _openGate.Dispose();
  }

  private async Task CloseCurrentConnection()
  {
    if (_connection is null)
    {
      return;
    }

    try
    {
      await _connection.DisposeAsync();
    }
    catch (Exception ex)
    {
      logger.LogDebug(ex, "Ignoring an error while closing the previous connection.");
    }

    _connection = null;
  }

  // Every method of TCallbacks becomes a handler the server can call by name.
  private void ListenForCallbacks(HubConnection connection)
  {
    var methods = typeof(TCallbacks).GetMethods(BindingFlags.Public | BindingFlags.Instance);
    if (methods.Length == 0)
    {
      return;
    }

    var target = services.GetRequiredService<TCallbacks>();
    foreach (var method in methods)
    {
      var parameterTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
      connection.On(method.Name, parameterTypes, args => RunCallback(target, method, args));
    }
  }

  private async Task<object?> RunCallback(TCallbacks target, MethodInfo method, object?[] args)
  {
    try
    {
      if (method.Invoke(target, args) is not Task task)
      {
        return null;
      }

      await task;
      return task.GetType().IsGenericType ? task.GetType().GetProperty(nameof(Task<object>.Result))?.GetValue(task) : null;
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Callback {Method} failed.", method.Name);
      return null;
    }
  }

  private async Task Raise(Func<Task>? handlers)
  {
    try
    {
      if (handlers is not null)
      {
        await handlers();
      }
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "A reconnect handler failed.");
    }
  }

  private async Task Raise(Func<Exception?, Task>? handlers, Exception? error)
  {
    try
    {
      if (handlers is not null)
      {
        await handlers(error);
      }
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "A connection-lost handler failed.");
    }
  }

  private sealed class BackoffRetryPolicy(Backoff backoff) : IRetryPolicy
  {
    // Never gives up: an agent must come back whenever the server does.
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
      return backoff.DelayFor((int)Math.Min(retryContext.PreviousRetryCount + 1, int.MaxValue));
    }
  }
}
