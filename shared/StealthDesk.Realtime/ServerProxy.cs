using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.SignalR.Client;

namespace StealthDesk.Realtime;

/// <summary>
/// Implements <typeparamref name="TServer"/> at runtime: each call becomes a hub invocation with the method's name.
/// </summary>
public class ServerProxy<TServer> : DispatchProxy
  where TServer : class
{
  private static readonly MethodInfo _invokeWithResult =
    typeof(ServerProxy<TServer>).GetMethod(nameof(InvokeWithResult), BindingFlags.NonPublic | BindingFlags.Static)!;

  private static readonly ConcurrentDictionary<Type, MethodInfo> _invokersByResultType = new();

  private Func<HubConnection> _connection = () => throw new InvalidOperationException("The channel is not open.");

  public static TServer Create(Func<HubConnection> connection)
  {
    var proxy = Create<TServer, ServerProxy<TServer>>();
    ((ServerProxy<TServer>)(object)proxy)._connection = connection;
    return proxy;
  }

  protected override object? Invoke(MethodInfo? method, object?[]? args)
  {
    ArgumentNullException.ThrowIfNull(method);

    var (arguments, cancellationToken) = SplitCancellationToken(args ?? []);
    var connection = _connection();

    if (method.ReturnType == typeof(Task))
    {
      return connection.InvokeCoreAsync(method.Name, arguments, cancellationToken);
    }

    if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
    {
      var resultType = method.ReturnType.GetGenericArguments()[0];
      var invoker = _invokersByResultType.GetOrAdd(resultType, type => _invokeWithResult.MakeGenericMethod(type));
      return invoker.Invoke(null, [connection, method.Name, arguments, cancellationToken]);
    }

    throw new NotSupportedException(
      $"{typeof(TServer).Name}.{method.Name} must return Task or Task<T> to be called over a realtime channel.");
  }

  private static Task<TResult> InvokeWithResult<TResult>(
    HubConnection connection,
    string methodName,
    object?[] arguments,
    CancellationToken cancellationToken)
  {
    return connection.InvokeCoreAsync<TResult>(methodName, arguments, cancellationToken);
  }

  // A trailing CancellationToken cancels the call; it is never sent to the server.
  private static (object?[] Arguments, CancellationToken Token) SplitCancellationToken(object?[] args)
  {
    return args is [.. var rest, CancellationToken token]
      ? (rest, token)
      : (args, CancellationToken.None);
  }
}
