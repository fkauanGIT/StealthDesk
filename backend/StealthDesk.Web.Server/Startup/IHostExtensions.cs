namespace StealthDesk.Web.Server.Startup;

public static class IHostExtensions
{
  public const string DefaultTenantName = "Default";

  public static async Task ApplyMigrations(this IHost host)
  {
    await using var scope = host.Services.CreateAsyncScope();
    await using var context = scope.ServiceProvider.GetRequiredService<AppDb>();
    if (context.Database.IsRelational())
    {
      await context.Database.MigrateAsync();
    }
  }

  public static async Task EnsureDatabaseCreated(this IHost host)
  {
    await using var scope = host.Services.CreateAsyncScope();
    await using var context = scope.ServiceProvider.GetRequiredService<AppDb>();
    await context.Database.EnsureCreatedAsync();
  }

  /// <summary>
  /// Creates a default tenant when the database has none, so agents can self-bootstrap
  /// before user accounts exist.
  /// </summary>
  public static async Task SeedDefaultTenant(this IHost host)
  {
    await using var scope = host.Services.CreateAsyncScope();
    var appDb = scope.ServiceProvider.GetRequiredService<AppDb>();

    if (await appDb.Tenants.AnyAsync())
    {
      return;
    }

    var tenant = new Tenant { Name = DefaultTenantName };
    appDb.Tenants.Add(tenant);
    await appDb.SaveChangesAsync();

    var logger = scope.ServiceProvider
      .GetRequiredService<ILoggerFactory>()
      .CreateLogger(typeof(IHostExtensions));

    logger.LogInformation("Created default tenant {TenantId}.", tenant.Id);
  }
}
