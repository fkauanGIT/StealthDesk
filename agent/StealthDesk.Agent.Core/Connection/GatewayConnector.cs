using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StealthDesk.Agent.Core.Heartbeat;
using StealthDesk.Agent.Core.Settings;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Core;
using StealthDesk.Realtime;

namespace StealthDesk.Agent.Core.Connection;

/// <summary>
/// Keeps trying to reach the server until it succeeds, then hands over to the channel's own reconnect.
/// Every time the connection is (re)established the server gets a fresh report right away.
/// </summary>
public sealed class GatewayConnector(
  IRealtimeChannel<IAgentGateway> channel,
  IHeartbeat heartbeat,
  ISettingsStore settings,
  Backoff retry,
  TimeProvider clock,
  ILogger<GatewayConnector> logger) : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (settings.Current.ServerUrl is not { } serverUrl)
    {
      logger.LogError("No server URL configured (Agent:ServerUrl); the agent can't connect.");
      return;
    }

    var endpoint = new Uri(serverUrl, Routes.AgentGateway);
    channel.Restored += () => heartbeat.SendNowAsync(stoppingToken);

    for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
    {
      if (await channel.OpenAsync(endpoint, UseWebSocketsOnly, stoppingToken))
      {
        logger.LogInformation("Connected to {Endpoint}.", endpoint);
        await heartbeat.SendNowAsync(stoppingToken);
        return;
      }

      var wait = retry.DelayFor(attempt);
      logger.LogInformation("Connection attempt {Attempt} failed; next one in {Wait}.", attempt, wait);

      try
      {
        await Task.Delay(wait, clock, stoppingToken);
      }
      catch (OperationCanceledException)
      {
        return;
      }
    }
  }

  // An agent keeps one long-lived connection, so it goes straight to WebSockets
  // and skips the transport negotiation round trip.
  private static void UseWebSocketsOnly(Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions options)
  {
    options.Transports = HttpTransportType.WebSockets;
    options.SkipNegotiation = true;
  }
}
