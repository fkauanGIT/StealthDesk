using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;

namespace StealthDesk.Realtime;

/// <summary>
/// A SignalR connection whose server methods are called through <typeparamref name="TServer"/>
/// instead of method-name strings, so renaming a hub method breaks the build.
/// </summary>
public interface IRealtimeChannel<out TServer> : IAsyncDisposable
  where TServer : class
{
  /// <summary>Calls on this object run the matching method on the server.</summary>
  TServer Server { get; }

  HubConnectionState State { get; }

  bool IsConnected => State == HubConnectionState.Connected;

  /// <summary>Raised after the connection drops and comes back on its own.</summary>
  event Func<Task>? Restored;

  /// <summary>Raised when the connection is closed for good (error or deliberate).</summary>
  event Func<Exception?, Task>? Lost;

  /// <summary>
  /// Makes one attempt to connect. Returns false if it fails; retrying is up to the caller.
  /// Once open, a dropped connection is re-established automatically.
  /// </summary>
  Task<bool> OpenAsync(Uri endpoint, Action<HttpConnectionOptions>? configure = null, CancellationToken cancellationToken = default);
}
