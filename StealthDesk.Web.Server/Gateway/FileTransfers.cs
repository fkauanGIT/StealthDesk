using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace StealthDesk.Web.Server.Gateway;

/// <summary>
/// Matches a file transfer's bytes to the HTTP request carrying them, by a transfer id the two sides never share a
/// scope to agree on otherwise: a download's HTTP request begins a transfer before asking the agent, whose chunks
/// then arrive through <see cref="IAgentGateway.SendFileChunks"/> on a connection of its own; an upload's HTTP
/// request feeds the same sink from the other end, for the agent to pull through <see cref="IAgentGateway.ReadUploadChunks"/>.
/// </summary>
public interface IFileTransfers
{
  /// <summary>Starts a transfer. Dispose it once whichever side began it is done with it.</summary>
  FileTransferSink Begin(Guid transferId);

  /// <summary>The transfer a chunk-carrying hub call names, if something is still waiting on it.</summary>
  FileTransferSink? Find(Guid transferId);
}

/// <summary>
/// A transfer in progress: a bounded channel one side writes into and the other reads from. Reading stops with a
/// <see cref="TimeoutException"/> if nothing arrives for a while, so a reader can't hang forever on a transfer the
/// other side never starts, or stops feeding midway.
/// </summary>
public sealed class FileTransferSink(Action<Guid> onDisposed, Guid transferId, TimeSpan idleTimeout) : IDisposable
{
  private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(8);
  private readonly CancellationTokenSource _done = new();
  private readonly TaskCompletionSource<string?> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

  public ChannelWriter<byte[]> Writer => _channel.Writer;

  /// <summary>
  /// How an upload finished once every chunk was through: null for success, a message for failure. Set by the
  /// agent's own report, since writing the bytes to disk can still fail after the last one arrives. A download
  /// never sets this; its HTTP response simply ends once the bytes stop, with nothing to report back.
  /// </summary>
  public Task<string?> Outcome => _outcome.Task;

  /// <summary>Reports how the far side finished. Only the first call (here or from disposal) has any effect.</summary>
  public void Complete(string? error) => _outcome.TrySetResult(error);

  /// <summary>
  /// Cancelled once the HTTP request is done with this transfer (refused for being too large, the browser closed
  /// it, or it finished), so a write still waiting for room in the channel doesn't block forever: nothing is ever
  /// going to read from it again.
  /// </summary>
  public CancellationToken Done => _done.Token;

  public async IAsyncEnumerable<byte[]> ReadChunks([EnumeratorCancellation] CancellationToken cancellationToken)
  {
    var reader = _channel.Reader;
    using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    idle.CancelAfter(idleTimeout);

    while (true)
    {
      bool more;
      try
      {
        more = await reader.WaitToReadAsync(idle.Token);
      }
      catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
      {
        throw new TimeoutException("The device stopped sending the file.");
      }

      if (!more)
      {
        yield break;
      }

      while (reader.TryRead(out var chunk))
      {
        yield return chunk;
        idle.CancelAfter(idleTimeout);
      }
    }
  }

  public void Dispose()
  {
    onDisposed(transferId);
    _done.Cancel();
    // A no-op once Complete already ran; otherwise nobody is coming to report an outcome at all.
    _outcome.TrySetResult("The transfer ended before the device could report its outcome.");
    _done.Dispose();
  }
}

public sealed class FileTransfers(IOptions<GatewayOptions> options) : IFileTransfers
{
  private readonly ConcurrentDictionary<Guid, FileTransferSink> _sinks = new();

  public FileTransferSink Begin(Guid transferId)
  {
    // Reused rather than a setting of its own: it already means "how long to wait on the agent", which fits a
    // transfer's chunks just as well as its first answer.
    var sink = new FileTransferSink(Remove, transferId, options.Value.AgentCallTimeout);
    _sinks[transferId] = sink;
    return sink;
  }

  public FileTransferSink? Find(Guid transferId) => _sinks.GetValueOrDefault(transferId);

  private void Remove(Guid transferId) => _sinks.TryRemove(transferId, out _);
}
