using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Web.Server.Data;
using StealthDesk.Web.Server.Data.Entities;
using StealthDesk.Web.Server.Startup;

namespace StealthDesk.Web.Server.Tests;

public class DefaultTenantTests
{
  [Fact]
  public async Task Startup_WithEmptyDatabase_CreatesDefaultTenant()
  {
    using var factory = new TestAppFactory();

    var tenants = await GetTenants(factory);

    var tenant = Assert.Single(tenants);
    Assert.Equal(IHostExtensions.DefaultTenantName, tenant.Name);
    Assert.NotEqual(Guid.Empty, tenant.Id);
  }

  [Fact]
  public async Task Startup_WhenRestarted_DoesNotCreateSecondTenant()
  {
    var databaseName = Guid.NewGuid().ToString("N");

    using (var firstRun = new TestAppFactory(databaseName))
    {
      Assert.Single(await GetTenants(firstRun));
    }

    using var secondRun = new TestAppFactory(databaseName);
    Assert.Single(await GetTenants(secondRun));
  }

  [Fact]
  public async Task Startup_WithSeparateDatabases_CreatesSeparateTenants()
  {
    using var factoryA = new TestAppFactory();
    using var factoryB = new TestAppFactory();

    var tenantA = Assert.Single(await GetTenants(factoryA));
    var tenantB = Assert.Single(await GetTenants(factoryB));

    Assert.NotEqual(tenantA.Id, tenantB.Id);
  }

  private static async Task<List<Tenant>> GetTenants(TestAppFactory factory)
  {
    using var scope = factory.Services.CreateScope();
    var appDb = scope.ServiceProvider.GetRequiredService<AppDb>();

    return await appDb.Tenants.ToListAsync(TestContext.Current.CancellationToken);
  }
}
