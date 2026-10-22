using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace StealthDesk.Web.Server.Persistence;

/// <summary>Ready only while the database answers: without it no page, sign-in or agent report works.</summary>
public sealed class DatabaseHealthCheck(StealthDeskDb db) : IHealthCheck
{
  public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
    await db.Database.CanConnectAsync(cancellationToken)
      ? HealthCheckResult.Healthy()
      : HealthCheckResult.Unhealthy("The database can't be reached.");
}
