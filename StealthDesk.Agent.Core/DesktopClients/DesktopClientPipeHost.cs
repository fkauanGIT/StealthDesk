using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StealthDesk.Agent.Core.DesktopClients;

/// <summary>
/// Serves the pipe the desktop clients connect to, one connection at a time: waits for a process, asks the operating
/// system who it is, and keeps the connection only if <see cref="DesktopClientAuthenticator"/> takes it. When the agent
/// stops, every client is told to close.
/// </summary>
public sealed class DesktopClientPipeHost(
  IDesktopClientPipeServer server,
  IPipeClientIdentity identity,
  DesktopClientAuthenticator authenticator,
  DesktopClientConnections connections,
  TimeProvider clock,
  ILogger<DesktopClientPipeHost> logger) : BackgroundService
{
  /// <summary>The pause between one connection and waiting for the next.</summary>
  public static readonly TimeSpan AcceptPause = TimeSpan.FromSeconds(3);

  /// <summary>Waits for one process to connect and keeps it if it is the desktop client: true when it was kept.</summary>
  public async Task<bool> AcceptOne(CancellationToken cancellationToken)
  {
    var pipe = server.Create();
    try
    {
      await pipe.WaitForConnectionAsync(cancellationToken);
    }
    catch
    {
      await pipe.DisposeAsync();
      throw;
    }

    var who = identity.Identify(pipe);
    if (!who.Succeeded)
    {
      logger.LogWarning("Refused a pipe connection: the process behind it couldn't be identified. {Error}", who.Error);
      await pipe.DisposeAsync();
      return false;
    }

    var client = who.Value!;
    if (authenticator.Refusal(client) is { } refusal)
    {
      logger.LogWarning("Refused the pipe connection of process {ProcessId}: {Refusal}", client.ProcessId, refusal);
      await pipe.DisposeAsync();
      return false;
    }

    connections.Add(client.ProcessId, pipe);
    logger.LogInformation("The desktop client in process {ProcessId} connected.", client.ProcessId);
    return true;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    using var timer = new PeriodicTimer(AcceptPause, clock);
    try
    {
      do
      {
        try
        {
          await AcceptOne(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
          logger.LogError(ex, "Serving the desktop client pipe failed.");
        }
      }
      while (await timer.WaitForNextTickAsync(stoppingToken));
    }
    catch (OperationCanceledException)
    {
      // Stopping.
    }
    finally
    {
      await connections.CloseAll("The agent is stopping.");
    }
  }
}
