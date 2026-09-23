using Microsoft.EntityFrameworkCore;
using StealthDesk.Web.Server.Data.Configuration;
using StealthDesk.Web.Server.Data.Entities;

namespace StealthDesk.Web.Server.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
  public DbSet<Device> Devices { get; init; }
  public DbSet<Tenant> Tenants { get; init; }

  protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
  {
    base.ConfigureConventions(configurationBuilder);
    configurationBuilder.Conventions.Add(_ => new DateTimeOffsetConvention());
    configurationBuilder.Conventions.Add(_ => new EntityBaseConvention());
  }

  protected override void OnModelCreating(ModelBuilder builder)
  {
    base.OnModelCreating(builder);

    ConfigureTenant(builder);
    ConfigureDevices(builder);
  }

  private void ConfigureDevices(ModelBuilder builder)
  {
    builder
      .Entity<Device>()
      .OwnsMany(x => x.Drives)
      .ToJson();
  }

  private void ConfigureTenant(ModelBuilder builder)
  {
    builder.Entity<Tenant>()
      .HasMany(t => t.Devices)
      .WithOne(d => d.Tenant)
      .HasForeignKey(d => d.TenantId)
      .OnDelete(DeleteBehavior.Cascade);
  }
}
