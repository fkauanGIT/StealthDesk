using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace StealthDesk.Web.Server.Gateway;

/// <summary>
/// Matches a file download's bytes, sent by the agent through <see cref="IAgentGateway.SendFileChunks"/>, to the
/// HTTP request streaming them to the browser. The two sides never share a scope: the HTTP request begins a
/// transfer before asking the agent, and the hub call that carries the bytes arrives on a connection of its own.
/// </summary>
public interface IFileTransfers
{
  /// <summary>Starts a transfer the HTTP request will read from. Dispose it once the request is done with it.</summary>
  FileTransferSink Begin(Guid transferId);

  /// <summary>The transfer a <see cref="IAgentGateway.SendFileChunks"/> call names, if the request is still waiting on it.</summary>
  FileTransferSink? Find(Guid transferId);
}

/// <summary>
/// A download in progress: a bounded channel the agent's chunks are written into and the HTTP request reads from.
/// Reading stops with a <see cref="TimeoutException"/> if nothing arrives for a while, so a request can't hang
/// forever on a transfer the agent never starts, or stops sending for mid-way.
/// </summary>
public sealed class FileTransferSink(Action<Guid> onDisposed, Guid transferId, TimeSpan idleTimeout) : IDisposable
{
  private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(8);
  private readonly CancellationTokenSource _done = new();

  public ChannelWriter<byte[]> Writer => _channel.Writer;

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
