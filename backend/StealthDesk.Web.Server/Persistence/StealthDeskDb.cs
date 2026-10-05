using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using StealthDesk.Web.Server.Accounts;

namespace StealthDesk.Web.Server.Persistence;

// Users without Identity roles: access is decided by StealthDesk's own permissions.
public class StealthDeskDb(DbContextOptions<StealthDeskDb> options, ITenantScope tenantScope)
  : IdentityUserContext<UserRecord, Guid>(options), IDataProtectionKeyContext
{
  // Read on every query, not when the context is created: signing in validates the cookie against the database,
  // which creates the request's context before the user is known.
  private Guid? CurrentTenantId => tenantScope.TenantId;

  public DbSet<TenantRecord> Tenants => Set<TenantRecord>();
  public DbSet<DeviceRecord> Devices => Set<DeviceRecord>();

  /// <summary>The keys that protect sign-in cookies, kept so sessions survive a restart.</summary>
  public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

  protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
  {
    // PostgreSQL only stores timestamps with a zero offset.
    configurationBuilder.Properties<DateTimeOffset>().HaveConversion<ToUtcConverter>();
  }

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    // Identity reads its schema version (passkeys need version 3) from IdentityOptions in the app's services.
    base.OnModelCreating(modelBuilder);

    modelBuilder.Entity<TenantRecord>(tenant =>
    {
      tenant.ToTable("tenants");
      tenant.Property(x => x.Name).HasMaxLength(TenantRecord.NameMax);
      tenant
        .HasMany(x => x.Devices)
        .WithOne(x => x.Tenant)
        .HasForeignKey(x => x.TenantId)
        .OnDelete(DeleteBehavior.Cascade);
      tenant
        .HasMany(x => x.Users)
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
      device.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
    });

    modelBuilder.Entity<UserRecord>(user =>
    {
      user.ToTable("users");
      user.Property(x => x.AccountType).HasConversion<string>().HasMaxLength(20);
      user.HasIndex(x => x.TenantId);
      user.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
    });

    modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
    modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
    modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
    modelBuilder.Entity<IdentityUserPasskey<Guid>>().ToTable("user_passkeys");
    modelBuilder.Entity<DataProtectionKey>().ToTable("data_protection_keys");
  }

  private sealed class ToUtcConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => value.ToUniversalTime(),
    value => value);
}
