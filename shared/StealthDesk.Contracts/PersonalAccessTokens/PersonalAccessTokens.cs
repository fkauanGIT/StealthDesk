using StealthDesk.Contracts.Permissions;

namespace StealthDesk.Contracts.PersonalAccessTokens;

/// <summary>How a token's permissions are decided.</summary>
public enum PersonalAccessTokenMode
{
  /// <summary>Only what the token was granted, and only while its owner can still do it.</summary>
  Restricted = 0,

  /// <summary>Whatever its owner can do.</summary>
  InheritOwner = 1,
}

/// <summary>A token as listed: never its secret.</summary>
/// <param name="PermissionCount">Grants of a restricted token; an inherit-owner token has none of its own.</param>
public sealed record PersonalAccessTokenSummary(
  Guid Id,
  Guid UserId,
  string Name,
  PersonalAccessTokenMode Mode,
  DateTimeOffset CreatedAt,
  DateTimeOffset? ExpiresAt,
  DateTimeOffset? LastUsed,
  int PermissionCount);

public sealed class PersonalAccessTokenList
{
  public IReadOnlyList<PersonalAccessTokenSummary> Items { get; init; } = [];
}

/// <summary>A permission a restricted token is granted, where.</summary>
public sealed record CredentialScope(string Permission, PermissionScopeKind ScopeKind, Guid? ScopeId = null);

/// <param name="ExpiresInDays">30, 90 or 365; null for a token that never expires.</param>
/// <param name="Scopes">What a restricted token may do; never more than its owner can.</param>
public sealed record CreatePersonalAccessTokenRequest(
  string Name,
  PersonalAccessTokenMode Mode = PersonalAccessTokenMode.Restricted,
  int? ExpiresInDays = CreatePersonalAccessTokenRequest.DefaultLifetimeDays,
  IReadOnlyList<CredentialScope>? Scopes = null)
{
  public const int NameMax = 256;
  public const int DefaultLifetimeDays = 90;

  public static IReadOnlyList<int> Lifetimes { get; } = [30, 90, 365];
}

/// <summary>The token as created, with its secret: shown this once and never again.</summary>
/// <param name="PlainTextToken">What scripts send in the <see cref="PersonalAccessTokenHeader.Name"/> header.</param>
public sealed record CreatedPersonalAccessToken(PersonalAccessTokenSummary Token, string PlainTextToken);

public sealed record RenamePersonalAccessTokenRequest(string Name);

public static class PersonalAccessTokenHeader
{
  public const string Name = "x-personal-token";
}
