using StealthDesk.Core.Security;
using StealthDesk.Web.Server.Dashboard;
using StealthDesk.Web.Server.Devices;
using StealthDesk.Web.Server.InstallerKeys;

namespace StealthDesk.Web.Server.Gateway;

/// <summary>
/// Decides whether a signed report is trusted and, if so, stores it. Kept apart from the hub so the rules
/// can be followed (and tested) without a SignalR connection.
/// </summary>
public sealed class ReportProcessor(
  IDeviceRegistry registry,
  IDeviceNotifier notifier,
  InstallerKeyService installerKeys,
  IMessageSigner signer,
  IOptionsSnapshot<GatewayOptions> options,
  TimeProvider clock,
  ILogger<ReportProcessor> logger)
{
  public async Task<GatewayReply<ReportReceipt>> ProcessAsync(
    SignedEnvelope<DeviceReport> envelope,
    ReportOrigin connection,
    CancellationToken cancellationToken = default)
  {
    var report = envelope.Payload;
    var known = report.DeviceId == Guid.Empty
      ? null
      : await registry.FindAsync(report.DeviceId, cancellationToken);

    // A known device signing with the key it registered is trusted as it is; any installer key it sends is ignored.
    if (known is not null && signer.HasValidSignature(envelope, known.PublicKey))
    {
      if (!IsRecent(envelope))
      {
        return Refuse(report, "Timestamp expired.");
      }

      var tenant = report.TenantId == Guid.Empty ? known.TenantId : report.TenantId;
      return await SaveAsync(report with { TenantId = tenant }, connection, known.PublicKey, allowNew: false, cancellationToken);
    }

    // Anything else takes an installer key: a new device joining, or a reinstalled one bringing a new signing key.
    if (string.IsNullOrEmpty(report.InstallerKey))
    {
      return Refuse(report, known is null ? "Unknown device." : "Signature verification failed.");
    }

    if (!MessageSigner.IsWellFormedPublicKey(envelope.SignerKey))
    {
      return Refuse(report, "Invalid public key.");
    }

    if (!signer.HasValidSignature(envelope, envelope.SignerKey))
    {
      return Refuse(report, "Signature verification failed.");
    }

    if (!IsRecent(envelope))
    {
      return Refuse(report, "Timestamp expired.");
    }

    var key = await installerKeys.CheckAsync(report.InstallerKey, cancellationToken);
    if (key is null)
    {
      return Refuse(report, "Invalid installer key.");
    }

    if (known is not null)
    {
      if (known.TenantId != key.TenantId)
      {
        return Refuse(report, "The device belongs to another tenant.");
      }

      if (!await installerKeys.MayReinstallAsync(key, known, cancellationToken))
      {
        return Refuse(report, "The installer key's creator may not install an agent on this device.");
      }
    }

    // The device joins the key's tenant, whatever an agent remembered from a server it reported to before.
    var deviceId = report.DeviceId == Guid.Empty ? Guid.NewGuid() : report.DeviceId;
    var address = connection.RemoteAddress is { IsIPv4MappedToIPv6: true } mapped ? mapped.MapToIPv4() : connection.RemoteAddress;
    if (!await installerKeys.RedeemAsync(key.Id, deviceId, address?.ToString(), cancellationToken))
    {
      return Refuse(report, "Invalid installer key.");
    }

    logger.LogInformation("Device {DeviceId} ({MachineName}) used installer key {KeyId}.", deviceId, report.MachineName, key.Id);
    return await SaveAsync(report with { DeviceId = deviceId, TenantId = key.TenantId }, connection, envelope.SignerKey, allowNew: true, cancellationToken);
  }

  private bool IsRecent(SignedEnvelope<DeviceReport> envelope) =>
    options.Value.ClockTolerance is not { } tolerance || signer.IsRecent(envelope, tolerance);

  private async Task<GatewayReply<ReportReceipt>> SaveAsync(
    DeviceReport report,
    ReportOrigin connection,
    string publicKey,
    bool allowNew,
    CancellationToken cancellationToken)
  {
    var saved = await registry.SaveReportAsync(report, connection, publicKey, allowNew, cancellationToken);
    if (!saved.Succeeded)
    {
      return Refuse(report, saved.Error);
    }

    var device = saved.Value!;
    await notifier.DeviceChangedAsync(device);
    return GatewayReply.Accept(new ReportReceipt(device.Id, device.TenantId, clock.GetUtcNow()));
  }

  private GatewayReply<ReportReceipt> Refuse(DeviceReport report, string reason)
  {
    logger.LogWarning("Refused report from device {DeviceId} ({MachineName}): {Reason}", report.DeviceId, report.MachineName, reason);
    return GatewayReply.Refuse<ReportReceipt>(reason);
  }
}
