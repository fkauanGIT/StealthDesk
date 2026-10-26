using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Contracts.Terminal;
using StealthDesk.Web.Server.Dashboard;
using StealthDesk.Web.Server.Devices;
using StealthDesk.Web.Server.Terminal;

namespace StealthDesk.Web.Server.Gateway;

/// <summary>The realtime endpoint agents stay connected to.</summary>
// No signed-in user here: agents prove who they are by signing every report with their device key.
[AllowAnonymous]
public sealed class AgentGatewayHub(
  ReportProcessor processor,
  IDeviceRegistry registry,
  IDeviceNotifier notifier,
  ITerminalRelay terminals,
  TimeProvider clock,
  ILogger<AgentGatewayHub> logger) : Hub<IAgentCallbacks>, IAgentGateway
{
  // Hub instances live for one call only; what must outlive it goes in the connection's items.
  private const string DeviceIdItem = "device-id";

  public async Task<GatewayReply<ReportReceipt>> SubmitReport(SignedEnvelope<DeviceReport> envelope)
  {
    try
    {
      var connection = new ReportOrigin(
        Context.ConnectionId,
        Context.GetHttpContext()?.Connection.RemoteIpAddress,
        clock.GetUtcNow());

      var reply = await processor.ProcessAsync(envelope, connection, Context.ConnectionAborted);
      if (reply is { Accepted: true, Value: { } receipt })
      {
        Context.Items[DeviceIdItem] = receipt.DeviceId;
      }

      return reply;
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Failed to process a report on connection {ConnectionId}.", Context.ConnectionId);
      return GatewayReply.Refuse<ReportReceipt>("The report could not be processed.");
    }
  }

  // The device is the one this connection reported as, proven by its signature: output can't be sent as another one.
  public async Task SendTerminalOutput(TerminalOutput output)
  {
    if (!Context.Items.TryGetValue(DeviceIdItem, out var value) || value is not Guid deviceId)
    {
      logger.LogWarning("Dropped output of terminal {TerminalId} from connection {ConnectionId}, which hasn't reported a device.", output.TerminalId, Context.ConnectionId);
      return;
    }

    try
    {
      await terminals.DeliverAsync(deviceId, output);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to deliver output of terminal {TerminalId}.", output.TerminalId);
    }
  }

  public override async Task OnDisconnectedAsync(Exception? exception)
  {
    try
    {
      if (Context.Items.TryGetValue(DeviceIdItem, out var value) && value is Guid deviceId)
      {
        var device = await registry.MarkOfflineAsync(deviceId, Context.ConnectionId, clock.GetUtcNow());
        if (device is not null)
        {
          await notifier.DeviceChangedAsync(device);
        }
      }
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Failed to mark the device of connection {ConnectionId} offline.", Context.ConnectionId);
    }
    finally
    {
      await base.OnDisconnectedAsync(exception);
    }
  }
}
