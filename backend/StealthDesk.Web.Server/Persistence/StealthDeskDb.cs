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
  public DbSet<PermissionAssignmentRecord> PermissionAssignments => Set<PermissionAssignmentRecord>();
  public DbSet<UserGroupRecord> UserGroups => Set<UserGroupRecord>();
  public DbSet<UserGroupMemberRecord> UserGroupMembers => Set<UserGroupMemberRecord>();
  public DbSet<TenantInviteRecord> TenantInvites => Set<TenantInviteRecord>();

  // No tenant filter: server-wide entries belong to no tenant, and every read names the tenant it wants.
  public DbSet<AuthorizationChangeRecord> AuthorizationChanges => Set<AuthorizationChangeRecord>();

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

    // Read by the permission evaluator for any principal, so it has no tenant filter; the evaluator applies the
    // tenant rules itself.
    modelBuilder.Entity<PermissionAssignmentRecord>(assignment =>
    {
      assignment.ToTable("permission_assignments");
      assignment.Property(x => x.Permission).HasMaxLength(PermissionAssignmentRecord.PermissionMax);
      assignment.Property(x => x.Notes).HasMaxLength(PermissionAssignmentRecord.NotesMax);
      assignment.Property(x => x.CreatedByKind).HasMaxLength(PermissionAssignmentRecord.CreatedByKindMax);
      assignment.Property(x => x.PrincipalKind).HasConversion<string>().HasMaxLength(30);
      assignment.Property(x => x.Effect).HasConversion<string>().HasMaxLength(10);
      assignment.Property(x => x.ScopeKind).HasConversion<string>().HasMaxLength(20);
      assignment.HasIndex(x => new { x.PrincipalKind, x.PrincipalId });
      assignment.HasIndex(x => x.OwningTenantId);
    });

    modelBuilder.Entity<TenantInviteRecord>(invite =>
    {
      invite.ToTable("tenant_invites");
      invite.Property(x => x.InviteeEmail).HasMaxLength(TenantInviteRecord.EmailMax);
      invite.Property(x => x.ActivationCode).HasMaxLength(TenantInviteRecord.ActivationCodeLength);
      invite.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
      invite.HasIndex(x => x.ActivationCode).IsUnique();
      invite.HasIndex(x => x.InviteeEmail).IsUnique();
      invite.HasIndex(x => x.TenantId);
      invite.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
    });

    modelBuilder.Entity<AuthorizationChangeRecord>(change =>
    {
      change.ToTable("authorization_changes");
      change.Property(x => x.Action).HasMaxLength(AuthorizationChangeRecord.ActionMax);
      change.Property(x => x.ActorKind).HasMaxLength(AuthorizationChangeRecord.ActorKindMax);
      change.Property(x => x.TargetKind).HasMaxLength(AuthorizationChangeRecord.TargetKindMax);
      change.Property(x => x.IpAddress).HasMaxLength(AuthorizationChangeRecord.IpAddressMax);
      change.Property(x => x.CorrelationId).HasMaxLength(AuthorizationChangeRecord.CorrelationIdMax);
      change.HasIndex(x => x.OwningTenantId);
      change.HasIndex(x => x.CreatedAt);
      change.HasIndex(x => x.ActorId);
      change.HasIndex(x => x.TargetId);
    });

    modelBuilder.Entity<UserGroupRecord>(group =>
    {
      group.ToTable("user_groups");
      group.Property(x => x.Name).HasMaxLength(UserGroupRecord.NameMax);
      group.Property(x => x.Description).HasMaxLength(UserGroupRecord.DescriptionMax);
      group.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
      group.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
      group.HasQueryFilter(x => CurrentTenantId == null || x.TenantId == CurrentTenantId);
    });

    modelBuilder.Entity<UserGroupMemberRecord>(member =>
    {
      member.ToTable("user_group_members");
      member.HasKey(x => new { x.UserGroupId, x.UserId });
      member.HasOne(x => x.UserGroup).WithMany(x => x.Members).HasForeignKey(x => x.UserGroupId).OnDelete(DeleteBehavior.Cascade);
      member.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
      member.HasIndex(x => x.UserId);
    });

    modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
    modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
    modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
    modelBuilder.Entity<IdentityUserPasskey<Guid>>(passkey =>
    {
      passkey.ToTable("user_passkeys");

      // Identity stores a passkey's data as an owned JSON document, which the in-memory provider reads back as
      // null. There it is stored as JSON text instead; PostgreSQL keeps its jsonb column.
      if (Database.IsInMemory())
      {
        passkey.Ignore(x => x.Data);
        passkey.Property(x => x.Data).HasConversion(
          data => System.Text.Json.JsonSerializer.Serialize(data, (System.Text.Json.JsonSerializerOptions?)null),
          json => System.Text.Json.JsonSerializer.Deserialize<IdentityPasskeyData>(json, (System.Text.Json.JsonSerializerOptions?)null)!);
      }
    });
    modelBuilder.Entity<DataProtectionKey>().ToTable("data_protection_keys");
  }

  private sealed class ToUtcConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => value.ToUniversalTime(),
    value => value);
}
