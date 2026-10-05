namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>Servers start without tenants (the first registration makes one), so tests that need one create it.</summary>
public static class TestTenants
{
  public const string Name = "Test tenant";

  public static Task<Guid> CreateAsync(ServerHost server, string name = Name) => server.WithDbAsync(async db =>
  {
    var tenant = new TenantRecord { Name = name };
    db.Tenants.Add(tenant);
    await db.SaveChangesAsync();
    return tenant.Id;
  });

  /// <summary>The server's only tenant, created if there is none yet.</summary>
  public static async Task<Guid> EnsureAsync(ServerHost server) =>
    await server.WithDbAsync(db => db.Tenants.Select(x => (Guid?)x.Id).FirstOrDefaultAsync()) ?? await CreateAsync(server);
}
