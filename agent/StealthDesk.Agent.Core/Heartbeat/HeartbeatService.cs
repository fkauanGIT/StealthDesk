using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StealthDesk.Agent.Core.Inventory;
using StealthDesk.Agent.Core.Settings;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Core.Security;
using StealthDesk.Realtime;

namespace StealthDesk.Agent.Core.Heartbeat;

public sealed class HeartbeatOptions
{
  public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Sends a signed report on demand.</summary>
public interface IHeartbeat
{
  Task SendNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Tells the server "this device is alive, and this is its state" on a fixed interval.
/// The first report also establishes the device's identity: the key pair is created once and kept.
/// </summary>
public sealed class HeartbeatService(
  IRealtimeChannel<IAgentGateway> channel,
  IDeviceInventory inventory,
  ISettingsStore settings,
  IMessageSigner signer,
  IOptions<HeartbeatOptions> options,
  TimeProvider clock,
  ILogger<HeartbeatService> logger) : BackgroundService, IHeartbeat
{
  // Connect, reconnect and the timer can all ask for a report at once; they go out one at a time.
  private readonly SemaphoreSlim _sending = new(1, 1);

  public async Task SendNowAsync(CancellationToken cancellationToken = default)
  {
    await _sending.WaitAsync(cancellationToken);
    try
    {
      if (!channel.IsConnected)
      {
        logger.LogDebug("Not connected; skipping heartbeat.");
        return;
      }

      var identity = settings.Current;
      var report = await inventory.CaptureAsync(cancellationToken) with
      {
        DeviceId = identity.DeviceId,
        TenantId = identity.TenantId,
      };

      var privateKey = await EnsurePrivateKeyAsync(identity, cancellationToken);
      var reply = await channel.Server.SubmitReport(signer.Sign(report, privateKey));

      if (!reply.Accepted || reply.Value is not { } receipt)
      {
        logger.LogWarning("The server refused the heartbeat: {Reason}", reply.Error);
        return;
      }

      if (receipt.DeviceId != identity.DeviceId || receipt.TenantId != identity.TenantId)
      {
        logger.LogInformation("Server registered this device as {DeviceId}; saving it.", receipt.DeviceId);
        await settings.SaveIdentityAsync(receipt.DeviceId, receipt.TenantId, cancellationToken);
      }

      logger.LogDebug("Heartbeat accepted.");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      logger.LogError(ex, "Heartbeat failed.");
    }
    finally
    {
      _sending.Release();
    }
  }

  public override void Dispose()
  {
    _sending.Dispose();
    base.Dispose();
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    using var timer = new PeriodicTimer(options.Value.Interval, clock);
    try
    {
      while (await timer.WaitForNextTickAsync(stoppingToken))
      {
        await SendNowAsync(stoppingToken);
      }
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
    }
  }

  private async Task<string> EnsurePrivateKeyAsync(AgentSettings identity, CancellationToken cancellationToken)
  {
    if (!string.IsNullOrWhiteSpace(identity.PrivateKey))
    {
      return identity.PrivateKey;
    }

    logger.LogInformation("First run: creating this device's key pair.");
    var keys = signer.CreateKeys();
    await settings.SavePrivateKeyAsync(keys.PrivateKey, cancellationToken);
    return keys.PrivateKey;
  }
}
