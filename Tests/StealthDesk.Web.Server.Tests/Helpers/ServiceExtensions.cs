using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Web.Server.Data;
using StealthDesk.Web.Server.Data.Entities;

namespace StealthDesk.Web.Server.Tests.Helpers;

public static class ServiceExtensions
{
  public static async Task<Tenant> CreateTestTenant(this IServiceProvider services, string tenantName = "Test Tenant")
  {
    using var scope = services.CreateScope();
    await using var db = scope.ServiceProvider.GetRequiredService<AppDb>();

    var tenant = new Tenant { Id = Guid.NewGuid(), Name = tenantName };
    db.Tenants.Add(tenant);
    await db.SaveChangesAsync();

    return tenant;
  }
}
