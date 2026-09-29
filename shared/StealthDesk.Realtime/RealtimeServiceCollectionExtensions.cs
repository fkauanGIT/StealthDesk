using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StealthDesk.Core;

namespace StealthDesk.Realtime;

public static class RealtimeServiceCollectionExtensions
{
  /// <summary>
  /// Registers a singleton <see cref="IRealtimeChannel{TServer}"/>. The server can call
  /// <typeparamref name="TCallbacks"/> methods, which are handled by <typeparamref name="TCallbacksHandler"/>.
  /// </summary>
  /// <remarks>
  /// The connection is built from the registered <see cref="IHubConnectionBuilder"/>. Register one before
  /// calling this to change the protocol (e.g. MessagePack).
  /// </remarks>
  public static IServiceCollection AddRealtimeChannel<TServer, TCallbacks, TCallbacksHandler>(
    this IServiceCollection services,
    Backoff? reconnectBackoff = null)
    where TServer : class
    where TCallbacks : class
    where TCallbacksHandler : class, TCallbacks
  {
    if (!typeof(TServer).IsInterface || !typeof(TCallbacks).IsInterface)
    {
      throw new ArgumentException("TServer and TCallbacks must be interfaces.");
    }

    var backoff = reconnectBackoff ?? new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));

    services.TryAddTransient<IHubConnectionBuilder, HubConnectionBuilder>();
    services.TryAddSingleton<TCallbacks, TCallbacksHandler>();
    services.TryAddSingleton<IRealtimeChannel<TServer>>(provider => new RealtimeChannel<TServer, TCallbacks>(
      provider,
      backoff,
      provider.GetRequiredService<ILogger<RealtimeChannel<TServer, TCallbacks>>>()));

    return services;
  }
}
