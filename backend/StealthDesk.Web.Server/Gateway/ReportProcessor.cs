using StealthDesk.Core.Security;
using StealthDesk.Web.Server.Devices;

namespace StealthDesk.Web.Server.Gateway;

/// <summary>
/// Decides whether a signed report is trusted and, if so, stores it. Kept apart from the hub so the rules
/// can be followed (and tested) without a SignalR connection.
/// </summary>
public sealed class ReportProcessor(
  IDeviceRegistry registry,
  StealthDeskDb db,
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
    var rules = options.Value;
    var report = envelope.Payload;

    // Trust on first use: a new device introduces its key; a known device must keep using that key.
    var registeredKey = report.DeviceId == Guid.Empty
      ? null
      : await registry.FindPublicKeyAsync(report.DeviceId, cancellationToken);

    if (registeredKey is null && !rules.AllowSelfRegistration)
    {
      return Refuse(report, "Unknown device.");
    }

    var trustedKey = registeredKey ?? envelope.SignerKey;
    if (!MessageSigner.IsWellFormedPublicKey(trustedKey))
    {
      return Refuse(report, "Invalid public key.");
    }

    if (!signer.HasValidSignature(envelope, trustedKey))
    {
      return Refuse(report, "Signature verification failed.");
    }

    if (rules.ClockTolerance is { } tolerance && !signer.IsRecent(envelope, tolerance))
    {
      return Refuse(report, "Timestamp expired.");
    }

    var tenant = await ResolveTenantAsync(report, rules, cancellationToken);
    if (!tenant.Succeeded)
    {
      return Refuse(report, tenant.Error);
    }

    var saved = await registry.SaveReportAsync(
      report with { TenantId = tenant.Value },
      connection,
      trustedKey,
      allowNew: rules.AllowSelfRegistration,
      cancellationToken);

    if (!saved.Succeeded)
    {
      return Refuse(report, saved.Error);
    }

    var device = saved.Value!;
    return GatewayReply.Accept(new ReportReceipt(device.Id, device.TenantId, clock.GetUtcNow()));
  }

  // The report's tenant wins; otherwise a known device keeps its own; otherwise self-registration
  // uses the only tenant there is (with several tenants there's no safe way to guess).
  private async Task<Outcome<Guid>> ResolveTenantAsync(DeviceReport report, GatewayOptions rules, CancellationToken cancellationToken)
  {
    var tenantId = report.TenantId;

    if (tenantId == Guid.Empty && report.DeviceId != Guid.Empty)
    {
      tenantId = await registry.FindTenantAsync(report.DeviceId, cancellationToken) ?? Guid.Empty;
    }

    if (tenantId == Guid.Empty && rules.AllowSelfRegistration)
    {
      var tenants = await db.Tenants.Select(x => x.Id).Take(2).ToListAsync(cancellationToken);
      if (tenants.Count != 1)
      {
        return Outcome.Failure<Guid>(tenants.Count == 0
          ? "No tenant available."
          : "Self-registration needs a single-tenant server.");
      }

      tenantId = tenants[0];
    }

    if (tenantId == Guid.Empty || !await db.Tenants.AnyAsync(x => x.Id == tenantId, cancellationToken))
    {
      return Outcome.Failure<Guid>("Invalid tenant.");
    }

    return tenantId;
  }

  private GatewayReply<ReportReceipt> Refuse(DeviceReport report, string reason)
  {
    logger.LogWarning("Refused report from device {DeviceId} ({MachineName}): {Reason}", report.DeviceId, report.MachineName, reason);
    return GatewayReply.Refuse<ReportReceipt>(reason);
  }
}
