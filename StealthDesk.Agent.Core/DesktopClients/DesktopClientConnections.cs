using System.Collections.Concurrent;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.DesktopClients;
using StealthDesk.Ipc;

namespace StealthDesk.Agent.Core.DesktopClients;

/// <summary>A desktop client that passed the checks, and its pipe.</summary>
public sealed class DesktopClientConnection
{
  internal DesktopClientConnection(int processId, PipeStream stream, ILogger logger)
  {
    ProcessId = processId;
    Pipe = Pipes.Open<IDesktopClientEndpoint>(stream, new AgentEndpoint(this, logger));
  }

  public int ProcessId { get; }

  public PipeConnection<IDesktopClientEndpoint> Pipe { get; }

  /// <summary>What the client said about itself, once it did.</summary>
  public DesktopClientHello? Hello { get; internal set; }
}

/// <summary>The desktop clients connected to this agent, by process id. A client leaves as soon as its pipe closes.</summary>
public sealed class DesktopClientConnections(ILogger<DesktopClientConnections> logger)
{
  /// <summary>How long each client gets to take the shutdown message when the agent stops.</summary>
  public static readonly TimeSpan ShutdownTimeLimit = TimeSpan.FromSeconds(2);

  private readonly ConcurrentDictionary<int, DesktopClientConnection> _clients = new();

  public IReadOnlyList<DesktopClientConnection> All => _clients.Values.ToList();

  public bool TryGet(int processId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DesktopClientConnection? client) =>
    _clients.TryGetValue(processId, out client);

  /// <summary>Opens the pipe of a client that passed the checks and keeps it until the pipe closes.</summary>
  public DesktopClientConnection Add(int processId, PipeStream stream)
  {
    var client = new DesktopClientConnection(processId, stream, logger);
    _clients.AddOrUpdate(
      processId,
      client,
      (_, earlier) =>
      {
        earlier.Pipe.Dispose();
        return client;
      });

    client.Pipe.Closed.ContinueWith(
      _ =>
      {
        if (_clients.TryRemove(new KeyValuePair<int, DesktopClientConnection>(processId, client)))
        {
          logger.LogInformation("The desktop client in process {ProcessId} disconnected.", processId);
        }

        client.Pipe.Dispose();
      },
      TaskScheduler.Default);

    return client;
  }

  /// <summary>Tells every client to close, then closes their pipes.</summary>
  public async Task CloseAll(string reason)
  {
    var clients = _clients.Values.ToList();
    _clients.Clear();
    await Task.WhenAll(clients.Select(async client =>
    {
      if (await client.Pipe.Send((remote, token) => remote.Shutdown(reason, token), ShutdownTimeLimit) is { } refusal)
      {
        logger.LogWarning("The desktop client in process {ProcessId} didn't take the shutdown: {Refusal}", client.ProcessId, refusal);
      }

      client.Pipe.Dispose();
    }));
  }
}

/// <summary>What the agent answers to one desktop client.</summary>
internal sealed class AgentEndpoint(DesktopClientConnection client, ILogger logger) : IAgentEndpoint
{
  public Task ClientReady(DesktopClientHello hello, CancellationToken cancellationToken)
  {
    client.Hello = hello;
    logger.LogInformation(
      "The desktop client of session {SessionId} (process {ProcessId}, version {Version}) is ready.",
      hello.SessionId,
      client.ProcessId,
      hello.Version);
    return Task.CompletedTask;
  }
}
