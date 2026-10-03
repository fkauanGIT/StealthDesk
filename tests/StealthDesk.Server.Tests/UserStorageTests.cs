using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>Users stored through ASP.NET Core Identity, against PostgreSQL.</summary>
public class UserStorageTests
{
  private const string Password = "Correct-horse-9";

  [Fact]
  public async Task Migrations_CreateTheAccountTables()
  {
    using var server = await ServerHost.OnPostgresAsync();

    var tables = await server.WithDbAsync(db => db.Database
      .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
      .ToListAsync());

    Assert.Subset(
      tables.ToHashSet(),
      new HashSet<string> { "users", "user_claims", "user_logins", "user_tokens", "user_passkeys", "data_protection_keys" });
  }

  [Fact]
  public async Task CreatedUser_IsReadBackWithItsTenant()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var tenantId = await DefaultTenant(server);

    var created = await Users(server, async users =>
    {
      var user = NewUser("ana@example.com", tenantId);
      var result = await users.CreateAsync(user, Password);
      Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(x => x.Description)));
      return user.Id;
    });

    var stored = await server.WithDbAsync(db => db.Users.Include(x => x.Tenant).SingleAsync(x => x.Id == created));
    Assert.Equal("ana@example.com", stored.Email);
    Assert.Equal(tenantId, stored.TenantId);
    Assert.Equal(DatabaseSetup.DefaultTenantName, stored.Tenant!.Name);
    Assert.Equal(AccountType.Member, stored.AccountType);
    Assert.False(stored.MustChangePassword);
  }

  [Fact]
  public async Task Password_IsStoredHashed()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var tenantId = await DefaultTenant(server);

    var (hash, matches) = await Users(server, async users =>
    {
      var user = NewUser("bruno@example.com", tenantId);
      await users.CreateAsync(user, Password);
      return (user.PasswordHash, await users.CheckPasswordAsync(user, Password));
    });

    Assert.NotNull(hash);
    Assert.DoesNotContain(Password, hash);
    Assert.True(matches);
  }

  [Fact]
  public async Task Email_IsUniqueAsUserName()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var tenantId = await DefaultTenant(server);

    var second = await Users(server, async users =>
    {
      await users.CreateAsync(NewUser("carla@example.com", tenantId), Password);
      return await users.CreateAsync(NewUser("CARLA@example.com", tenantId), Password);
    });

    Assert.False(second.Succeeded);
    Assert.Contains(second.Errors, x => x.Code == "DuplicateUserName");
  }

  [Fact]
  public async Task DeletingATenant_DeletesItsUsers()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var tenantId = await DefaultTenant(server);
    await Users(server, users => users.CreateAsync(NewUser("dani@example.com", tenantId), Password));

    await server.WithDbAsync(async db =>
    {
      db.Tenants.Remove(await db.Tenants.SingleAsync(x => x.Id == tenantId));
      return await db.SaveChangesAsync();
    });

    Assert.False(await server.WithDbAsync(db => db.Users.AnyAsync()));
  }

  [Fact]
  public async Task Passkey_IsStoredForItsUser()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var tenantId = await DefaultTenant(server);
    byte[] credentialId = [1, 2, 3, 4];

    var stored = await Users(server, async users =>
    {
      var user = NewUser("edu@example.com", tenantId);
      await users.CreateAsync(user, Password);
      var passkey = new UserPasskeyInfo(credentialId, publicKey: [9, 9], DateTimeOffset.UtcNow, signCount: 0,
        transports: ["internal"], isUserVerified: true, isBackupEligible: false, isBackedUp: false,
        attestationObject: [7], clientDataJson: [8]);
      await users.AddOrUpdatePasskeyAsync(user, passkey);
      return await users.GetPasskeyAsync(user, credentialId);
    });

    Assert.NotNull(stored);
    Assert.Equal([9, 9], stored.PublicKey);
  }

  [Fact]
  public async Task DataProtectionKeys_AreKeptInTheDatabase()
  {
    using var server = await ServerHost.OnPostgresAsync();

    var protector = server.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
    var secret = protector.Protect("sign-in cookie");

    Assert.True(await server.WithDbAsync(db => db.DataProtectionKeys.AnyAsync()));

    // A restarted server reads the same keys and can still open what the first one protected.
    using var restarted = server.Restarted();
    var reopened = restarted.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(secret);
    Assert.Equal("sign-in cookie", reopened);
  }

  private static UserRecord NewUser(string email, Guid tenantId) => new() { UserName = email, Email = email, TenantId = tenantId };

  private static Task<Guid> DefaultTenant(ServerHost server) =>
    server.WithDbAsync(db => db.Tenants.Select(x => x.Id).SingleAsync());

  private static async Task<T> Users<T>(ServerHost server, Func<UserManager<UserRecord>, Task<T>> action)
  {
    await using var scope = server.Services.CreateAsyncScope();
    return await action(scope.ServiceProvider.GetRequiredService<UserManager<UserRecord>>());
  }
}
