using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Web.Server.Devices;

namespace StealthDesk.Web.Server.Gateway;

/// <summary>Calls on one device's agent, over the connection the agent keeps open to the gateway.</summary>
public interface IAgentCalls
{
  /// <summary>
  /// Runs <paramref name="call"/> on the agent of <paramref name="deviceId"/> and answers its reply. A device that is
  /// offline, unknown, or doesn't answer within <see cref="GatewayOptions.AgentCallTimeout"/> gets a refusal that says
  /// so. Doesn't check who is asking: callers check the user's permission on the device first.
  /// </summary>
  Task<GatewayReply<T>> CallAsync<T>(
    Guid deviceId,
    Func<IAgentCallbacks, CancellationToken, Task<GatewayReply<T>>> call,
    CancellationToken cancellationToken = default);
}

public sealed class AgentCalls(
  IHubContext<AgentGatewayHub, IAgentCallbacks> hub,
  IDeviceRegistry registry,
  IOptions<GatewayOptions> options,
  ILogger<AgentCalls> logger) : IAgentCalls
{
  public async Task<GatewayReply<T>> CallAsync<T>(
    Guid deviceId,
    Func<IAgentCallbacks, CancellationToken, Task<GatewayReply<T>>> call,
    CancellationToken cancellationToken = default)
  {
    // The connection a device reported on last is the one to call: a reconnect replaces it, a disconnect clears it.
    var device = await registry.FindAsync(deviceId, cancellationToken);
    if (device is not { IsOnline: true, ConnectionId.Length: > 0 })
    {
      return GatewayReply.Refuse<T>("The device is offline.");
    }

    var timeout = options.Value.AgentCallTimeout;
    using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    limit.CancelAfter(timeout);

    try
    {
      return await call(hub.Clients.Client(device.ConnectionId), limit.Token)
        ?? GatewayReply.Refuse<T>("The device gave no answer.");
    }
    // SignalR reports its own cancellation as a HubException: the time limit is told by the token, not the type.
    catch (Exception ex) when (ex is OperationCanceledException or HubException
      && limit.IsCancellationRequested
      && !cancellationToken.IsCancellationRequested)
    {
      logger.LogWarning("Device {DeviceId} didn't answer within {Timeout}.", deviceId, timeout);
      return GatewayReply.Refuse<T>($"The device didn't answer within {timeout.TotalSeconds:0} seconds.");
    }
    catch (IOException ex)
    {
      // The connection closed before the answer came, or was already gone.
      logger.LogInformation(ex, "Device {DeviceId} disconnected during a call.", deviceId);
      return GatewayReply.Refuse<T>("The device went offline before answering.");
    }
    catch (HubException ex)
    {
      logger.LogWarning(ex, "The agent of device {DeviceId} failed to answer a call.", deviceId);
      return GatewayReply.Refuse<T>("The device couldn't carry out the request.");
    }
  }
}
