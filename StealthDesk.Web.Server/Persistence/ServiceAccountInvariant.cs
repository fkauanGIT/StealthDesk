using Microsoft.EntityFrameworkCore.Diagnostics;
using StealthDesk.Contracts.ServiceAccounts;

namespace StealthDesk.Web.Server.Persistence;

/// <summary>
/// Refuses to save a server account with a tenant or a tenant account without one. PostgreSQL enforces this with
/// a check constraint; the in-memory database has none, so it is checked here too.
/// </summary>
public sealed class ServiceAccountInvariant : SaveChangesInterceptor
{
  public static readonly ServiceAccountInvariant Instance = new();

  public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
  {
    Check(eventData.Context);
    return result;
  }

  public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
    DbContextEventData eventData,
    InterceptionResult<int> result,
    CancellationToken cancellationToken = default)
  {
    Check(eventData.Context);
    return ValueTask.FromResult(result);
  }

  private static void Check(DbContext? context)
  {
    foreach (var entry in context?.ChangeTracker.Entries<ServiceAccountRecord>() ?? [])
    {
      if (entry.State is not (EntityState.Added or EntityState.Modified))
      {
        continue;
      }

      if ((entry.Entity.Kind == ServiceAccountKind.Server) != (entry.Entity.TenantId is null))
      {
        throw new InvalidOperationException("A server service account has no tenant, and a tenant service account always has one.");
      }
    }
  }
}
