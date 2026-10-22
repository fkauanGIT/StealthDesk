using StealthDesk.Contracts.PersonalAccessTokens;

namespace StealthDesk.Web.Server.Persistence;

/// <summary>A user's token for scripts. Only a hash of its secret is kept, like a password.</summary>
public class PersonalAccessTokenRecord
{
  public const int NameMax = CreatePersonalAccessTokenRequest.NameMax;
  public const int HashMax = 256;

  public Guid Id { get; set; }

  public Guid UserId { get; set; }
  public UserRecord? User { get; set; }

  public string Name { get; set; } = string.Empty;
  public string HashedSecret { get; set; } = string.Empty;
  public PersonalAccessTokenMode Mode { get; set; }

  public DateTimeOffset CreatedAt { get; set; }

  /// <summary>None for a token that never expires.</summary>
  public DateTimeOffset? ExpiresAt { get; set; }

  public DateTimeOffset? LastUsed { get; set; }
}
