using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.DesktopClients;
using StealthDesk.Ipc;

namespace StealthDesk.DesktopClient;

/// <summary>
/// Keeps the desktop client connected to its agent's pipe: connects, says who it is, and when the pipe closes, as when
/// the agent restarts, connects again. After <see cref="GiveUpAfter"/> without reaching the agent it stops the client,
/// since an agent that is back starts a new one.
/// </summary>
internal sealed class AgentConnection(
  string pipeName,
  DesktopClientHello hello,
  IDesktopClientEndpoint endpoint,
  IHostApplicationLifetime lifetime,
  TimeProvider clock,
  ILogger<AgentConnection> logger) : BackgroundService
{
  public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);
  public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(1);

  /// <summary>How long one attempt waits for the agent's pipe to take the connection.</summary>
  public static readonly TimeSpan ConnectTimeLimit = TimeSpan.FromSeconds(5);

  /// <summary>The open connection, or null between connections.</summary>
  public PipeConnection<IAgentEndpoint>? Current { get; private set; }

  /// <summary>Whether it stopped the client for not reaching the agent: the client then exits with 1.</summary>
  public bool GaveUp { get; private set; }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    DateTimeOffset? failingSince = null;
    try
    {
      while (!stoppingToken.IsCancellationRequested)
      {
        if (await ConnectOnce(stoppingToken))
        {
          failingSince = null;
        }
        else
        {
          failingSince ??= clock.GetUtcNow();
          if (clock.GetUtcNow() - failingSince >= GiveUpAfter)
          {
            logger.LogError("The agent couldn't be reached for {GiveUpAfter}; closing.", GiveUpAfter);
            GaveUp = true;
            lifetime.StopApplication();
            return;
          }
        }

        await Task.Delay(RetryEvery, clock, stoppingToken);
      }
    }
    catch (OperationCanceledException)
    {
      // Stopping.
    }
  }

  /// <summary>One connection, until it closes: true when the agent took it, false when it couldn't be made.</summary>
  private async Task<bool> ConnectOnce(CancellationToken cancellationToken)
  {
    try
    {
      var stream = await Pipes.ConnectAsync(pipeName, ConnectTimeLimit, cancellationToken);
      using var pipe = Pipes.Open<IAgentEndpoint>(stream, endpoint);
      if (await pipe.Send((agent, token) => agent.ClientReady(hello, token), cancellationToken: cancellationToken) is { } refusal)
      {
        logger.LogWarning("The agent didn't take the connection on {PipeName}: {Refusal}", pipeName, refusal);
        return false;
      }

      Current = pipe;
      logger.LogInformation("Connected to the agent on {PipeName}.", pipeName);
      await pipe.Closed.WaitAsync(cancellationToken);
      logger.LogWarning("The connection to the agent closed.");
      return true;
    }
    catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
    {
      logger.LogWarning("The agent's pipe {PipeName} can't be reached: {Error}", pipeName, ex.Message);
      return false;
    }
    finally
    {
      Current = null;
    }
  }
}
