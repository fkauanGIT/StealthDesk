using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace StealthDesk.Web.Server.Persistence;

public class StealthDeskDb(DbContextOptions<StealthDeskDb> options) : DbContext(options)
{
  public DbSet<TenantRecord> Tenants => Set<TenantRecord>();
  public DbSet<DeviceRecord> Devices => Set<DeviceRecord>();

  protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
  {
    // PostgreSQL only stores timestamps with a zero offset.
    configurationBuilder.Properties<DateTimeOffset>().HaveConversion<ToUtcConverter>();
  }

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.Entity<TenantRecord>(tenant =>
    {
      tenant.ToTable("tenants");
      tenant.Property(x => x.Name).HasMaxLength(TenantRecord.NameMax);
      tenant
        .HasMany(x => x.Devices)
        .WithOne(x => x.Tenant)
        .HasForeignKey(x => x.TenantId)
        .OnDelete(DeleteBehavior.Cascade);
    });

    modelBuilder.Entity<DeviceRecord>(device =>
    {
      device.ToTable("devices");
      device.Property(x => x.Name).HasMaxLength(DeviceRecord.NameMax);
      device.Property(x => x.Alias).HasMaxLength(DeviceRecord.NameMax);
      device.Property(x => x.DnsName).HasMaxLength(DeviceRecord.DnsNameMax);
      device.Property(x => x.AgentVersion).HasMaxLength(DeviceRecord.AgentVersionMax);
      device.Property(x => x.OsDescription).HasMaxLength(DeviceRecord.OsDescriptionMax);
      device.Property(x => x.LocalIpV4).HasMaxLength(DeviceRecord.IpV4Max);
      device.Property(x => x.LocalIpV6).HasMaxLength(DeviceRecord.IpV6Max);
      device.Property(x => x.PublicIpV4).HasMaxLength(DeviceRecord.IpV4Max);
      device.Property(x => x.PublicIpV6).HasMaxLength(DeviceRecord.IpV6Max);
      device.Property(x => x.PublicKey).HasMaxLength(DeviceRecord.PublicKeyMax);
      device.Property(x => x.Platform).HasConversion<string>().HasMaxLength(20);
      device.Property(x => x.OsArchitecture).HasConversion<string>().HasMaxLength(20);
      device.OwnsMany(x => x.Disks, disks => disks.ToJson());
      device.HasIndex(x => x.TenantId);
    });
  }

  private sealed class ToUtcConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => value.ToUniversalTime(),
    value => value);
}
