using System.Globalization;
using System.IO.Pipes;
using StreamJsonRpc;

namespace StealthDesk.Ipc;

/// <summary>What the other end of a pipe answered, or why it didn't.</summary>
public readonly record struct PipeAnswer<T>(T? Value, string? Refusal)
{
  public bool Answered => Refusal is null;
}

/// <summary>
/// One end of an open pipe. Calls on <see cref="Remote"/> run on the other end; calls from the other end run on the
/// local endpoint it was opened with. Each message is MessagePack preceded by its length.
/// </summary>
public sealed class PipeConnection<TRemote> : IDisposable
  where TRemote : class
{
  /// <summary>How long a call waits for the other end when the caller gives no time limit.</summary>
  public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromSeconds(10);

  private readonly PipeStream _stream;
  private readonly JsonRpc _rpc;

  internal PipeConnection(PipeStream stream, object localEndpoint)
  {
    _stream = stream;
    _rpc = new JsonRpc(new LengthHeaderMessageHandler(stream, stream, new MessagePackFormatter()), localEndpoint);
    Remote = _rpc.Attach<TRemote>();
    Closed = _rpc.Completion.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    _rpc.StartListening();
  }

  /// <summary>
  /// Calls on this run on the other end, with no time limit and throwing when it fails: <see cref="Ask"/> and
  /// <see cref="Send"/> are the usual way.
  /// </summary>
  public TRemote Remote { get; }

  /// <summary>Completes when either end closes the pipe or it breaks, for instance because the other process died.</summary>
  public Task Closed { get; }

  public bool IsConnected => _stream.IsConnected && !Closed.IsCompleted;

  /// <summary>
  /// Runs <paramref name="call"/> on the other end and waits for its answer up to <paramref name="timeLimit"/>
  /// (<see cref="DefaultTimeLimit"/> when null). The other end refusing, the time running out and the pipe closing
  /// come back as the reason; only <paramref name="cancellationToken"/> throws.
  /// </summary>
  public async Task<PipeAnswer<T>> Ask<T>(
    Func<TRemote, CancellationToken, Task<T>> call,
    TimeSpan? timeLimit = null,
    CancellationToken cancellationToken = default)
  {
    var limit = timeLimit ?? DefaultTimeLimit;
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(limit);
    try
    {
      return new PipeAnswer<T>(await call(Remote, timeout.Token), null);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      return new PipeAnswer<T>(default, Closed.IsCompleted ? Gone : string.Create(CultureInfo.InvariantCulture, $"No answer within {limit.TotalSeconds:0.###} s."));
    }
    catch (RemoteInvocationException ex)
    {
      return new PipeAnswer<T>(default, ex.Message);
    }
    catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException)
    {
      return new PipeAnswer<T>(default, Gone);
    }
  }

  /// <summary>Like <see cref="Ask"/>, for a call with nothing to answer: null once it ran, otherwise why it didn't.</summary>
  public async Task<string?> Send(
    Func<TRemote, CancellationToken, Task> call,
    TimeSpan? timeLimit = null,
    CancellationToken cancellationToken = default)
  {
    var answer = await Ask(
      async (remote, token) =>
      {
        await call(remote, token);
        return true;
      },
      timeLimit,
      cancellationToken);
    return answer.Refusal;
  }

  public void Dispose()
  {
    _rpc.Dispose();
    _stream.Dispose();
  }

  private const string Gone = "The other end of the pipe is gone.";
}
