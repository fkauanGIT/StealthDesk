using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Contracts.InstallerKeys;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>Installer keys stored straight into the database, for tests about what agents do with them.</summary>
public static class TestInstallerKeys
{
  /// <summary>Creates a key and answers what an agent is given: <c>{id}:{secret}</c>.</summary>
  public static async Task<string> CreateAsync(
    ServerHost server,
    Guid tenantId,
    InstallerKeyType type = InstallerKeyType.Persistent,
    int? allowedUses = null,
    DateTimeOffset? expiresAt = null,
    InstallerKeyCreatorKind creatorKind = InstallerKeyCreatorKind.User,
    Guid? creatorId = null)
  {
    var hasher = server.Services.GetRequiredService<IPasswordHasher<InstallerKeyRecord>>();
    var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var key = new InstallerKeyRecord
    {
      Id = Guid.NewGuid(),
      TenantId = tenantId,
      Name = "Test key",
      Type = type,
      CreatorKind = creatorKind,
      CreatorId = creatorId ?? Guid.NewGuid(),
      CreatedAt = DateTimeOffset.UtcNow,
      AllowedUses = allowedUses,
      ExpiresAt = type == InstallerKeyType.UsageBased ? expiresAt ?? DateTimeOffset.UtcNow.AddHours(24) : expiresAt,
    };
    key.HashedSecret = hasher.HashPassword(key, secret);
    await server.WithDbAsync(async db =>
    {
      db.InstallerKeys.Add(key);
      return await db.SaveChangesAsync();
    });
    return $"{key.Id}:{secret}";
  }

  public static Guid IdOf(string fullKey) => Guid.Parse(fullKey.Split(':')[0]);
}
