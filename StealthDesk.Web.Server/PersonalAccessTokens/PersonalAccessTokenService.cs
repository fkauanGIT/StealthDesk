using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.PersonalAccessTokens;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.AuthorizationLogs;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.PersonalAccessTokens;

/// <summary>A token as it was created or removed, for the authorization change log.</summary>
public sealed record PersonalAccessTokenSnapshot(string Name, PersonalAccessTokenMode Mode, DateTimeOffset? ExpiresAt, int Grants);

/// <summary>
/// Users' tokens for scripts: created with a secret shown once, stored hashed, recognized from the header, and
/// removed with their grants. The secret is <c>{token id}:{64 random characters}</c>.
/// </summary>
public sealed class PersonalAccessTokenService(
  StealthDeskDb db,
  IPermissionEvaluator evaluator,
  IAuthorizationChangeFactory changes,
  IPasswordHasher<PersonalAccessTokenRecord> hasher,
  TimeProvider clock)
{
  private const string SecretAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  private const int SecretLength = 64;

  public async Task<IReadOnlyList<PersonalAccessTokenSummary>> ListAsync(Guid userId, CancellationToken cancellationToken)
  {
    var tokens = await Tokens().Where(x => x.UserId == userId).OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
    var ids = tokens.Select(x => x.Id).ToList();
    var grants = await db.PermissionAssignments.IgnoreQueryFilters()
      .Where(x => x.PrincipalKind == PermissionPrincipalKind.PersonalAccessToken && ids.Contains(x.PrincipalId))
      .GroupBy(x => x.PrincipalId)
      .Select(x => new { x.Key, Count = x.Count() })
      .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
    return [.. tokens.Select(x => Summary(x, grants.GetValueOrDefault(x.Id)))];
  }

  /// <summary>Creates a token for a user of the tenant, with the grants a restricted token asks for.</summary>
  public async Task<IResult> CreateAsync(Principal actor, Guid ownerId, Guid tenant, CreatePersonalAccessTokenRequest request, CancellationToken cancellationToken)
  {
    if (NameRefusal(request.Name) is { } invalidName)
    {
      return Invalid(invalidName);
    }

    if (request.ExpiresInDays is { } days && !CreatePersonalAccessTokenRequest.Lifetimes.Contains(days))
    {
      return Invalid($"Choose an expiry of {string.Join(", ", CreatePersonalAccessTokenRequest.Lifetimes)} days, or none.");
    }

    if (!Enum.IsDefined(request.Mode))
    {
      return Invalid("Choose a permission mode.");
    }

    // An inheriting token reaches all its owner can do; something acting through a token must not mint one.
    if (request.Mode == PersonalAccessTokenMode.InheritOwner && actor.IsCredential)
    {
      return Invalid("Inherit-owner tokens can only be created from a signed-in session. Use a restricted token with explicit grants.");
    }

    var scopes = request.Scopes ?? [];
    if (request.Mode == PersonalAccessTokenMode.InheritOwner && scopes.Count > 0)
    {
      return Invalid("An inherit-owner token takes its owner's permissions; leave its grants out or make it restricted.");
    }

    foreach (var scope in scopes)
    {
      if ((await GrantScopes.RefusalAsync(db, scope.Permission, PermissionEffect.Allow, scope.ScopeKind, scope.ScopeId, tenant, cancellationToken)
          ?? await GrantScopes.BeyondOwnerAsync(evaluator, ownerId, tenant, scope.Permission, PermissionEffect.Allow, scope.ScopeKind, scope.ScopeId, cancellationToken))
          is { } refusal)
      {
        return Invalid(refusal);
      }
    }

    var now = clock.GetUtcNow();
    var token = new PersonalAccessTokenRecord
    {
      Id = Guid.NewGuid(),
      UserId = ownerId,
      Name = request.Name.Trim(),
      Mode = request.Mode,
      CreatedAt = now,
      ExpiresAt = request.ExpiresInDays is { } lifetime ? now.AddDays(lifetime) : null,
    };
    var secret = RandomNumberGenerator.GetString(SecretAlphabet, SecretLength);
    token.HashedSecret = hasher.HashPassword(token, secret);
    db.PersonalAccessTokens.Add(token);

    var grants = scopes
      .DistinctBy(x => (x.Permission, x.ScopeKind, x.ScopeId))
      .Select(x => new PermissionAssignmentRecord
      {
        Id = Guid.NewGuid(),
        PrincipalKind = PermissionPrincipalKind.PersonalAccessToken,
        PrincipalId = token.Id,
        Permission = x.Permission,
        Effect = PermissionEffect.Allow,
        ScopeKind = x.ScopeKind,
        ScopeId = x.ScopeKind switch
        {
          PermissionScopeKind.Server => null,
          PermissionScopeKind.Tenant => tenant,
          _ => x.ScopeId,
        },
        OwningTenantId = x.ScopeKind == PermissionScopeKind.Server ? null : tenant,
        CreatedByKind = actor.Kind.ToString(),
        CreatedById = actor.Id,
      })
      .ToList();
    db.PermissionAssignments.AddRange(grants);

    db.AuthorizationChanges.Add(changes.Create(
      AuthorizationChangeActions.PersonalAccessTokenCreated,
      actor,
      AuthorizationChangeTargets.PersonalAccessToken,
      token.Id,
      tenant,
      after: new PersonalAccessTokenSnapshot(token.Name, token.Mode, token.ExpiresAt, grants.Count)));
    foreach (var grant in grants)
    {
      db.AuthorizationChanges.Add(changes.Create(
        AuthorizationChangeActions.PermissionAssignmentCreated,
        actor,
        AuthorizationChangeTargets.PermissionAssignment,
        grant.Id,
        grant.OwningTenantId,
        after: PermissionAssignmentSnapshot.Of(grant)));
    }

    await db.SaveChangesAsync(cancellationToken);
    return Results.Created(Routes.PersonalAccessToken(token.Id), new CreatedPersonalAccessToken(Summary(token, grants.Count), $"{token.Id}:{secret}"));
  }

  /// <summary>
  /// The bootstrap administrator's token, with the id and secret from configuration so automation knows it in
  /// advance: it acts as its owner and doesn't expire. Logged as the server's own change.
  /// </summary>
  public async Task BootstrapAsync(UserRecord owner, Guid id, string secret, CancellationToken cancellationToken)
  {
    var token = new PersonalAccessTokenRecord
    {
      Id = id,
      UserId = owner.Id,
      Name = "Bootstrap token",
      Mode = PersonalAccessTokenMode.InheritOwner,
      CreatedAt = clock.GetUtcNow(),
    };
    token.HashedSecret = hasher.HashPassword(token, secret);
    db.PersonalAccessTokens.Add(token);
    db.AuthorizationChanges.Add(changes.Create(
      AuthorizationChangeActions.PersonalAccessTokenCreated,
      actor: null,
      AuthorizationChangeTargets.PersonalAccessToken,
      token.Id,
      owner.TenantId,
      after: new PersonalAccessTokenSnapshot(token.Name, token.Mode, token.ExpiresAt, Grants: 0)));
    await db.SaveChangesAsync(cancellationToken);
  }

  public async Task<IResult> RenameAsync(Guid ownerId, Guid id, string name, CancellationToken cancellationToken)
  {
    if (NameRefusal(name) is { } invalid)
    {
      return Invalid(invalid);
    }

    if (await Tokens().FirstOrDefaultAsync(x => x.Id == id && x.UserId == ownerId, cancellationToken) is not { } token)
    {
      return Results.NotFound();
    }

    token.Name = name.Trim();
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(Summary(token, await GrantCountAsync(id, cancellationToken)));
  }

  /// <summary>Removes the token and its grants: a new token can never pick up what an old one held.</summary>
  public async Task<IResult> DeleteAsync(Principal actor, Guid ownerId, Guid tenant, Guid id, CancellationToken cancellationToken)
  {
    if (await Tokens().FirstOrDefaultAsync(x => x.Id == id && x.UserId == ownerId, cancellationToken) is not { } token)
    {
      return Results.NotFound();
    }

    var grants = await db.PermissionAssignments.IgnoreQueryFilters()
      .Where(x => x.PrincipalKind == PermissionPrincipalKind.PersonalAccessToken && x.PrincipalId == id)
      .ToListAsync(cancellationToken);
    db.PermissionAssignments.RemoveRange(grants);
    db.PersonalAccessTokens.Remove(token);
    db.AuthorizationChanges.Add(changes.Create(
      AuthorizationChangeActions.PersonalAccessTokenDeleted,
      actor,
      AuthorizationChangeTargets.PersonalAccessToken,
      id,
      tenant,
      before: new PersonalAccessTokenSnapshot(token.Name, token.Mode, token.ExpiresAt, grants.Count)));
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
  }

  /// <summary>
  /// The token an <c>id:secret</c> header value stands for, when it exists, hasn't expired and its secret matches;
  /// its last use is recorded.
  /// </summary>
  public async Task<PersonalAccessTokenRecord?> ValidateAsync(string presented, CancellationToken cancellationToken)
  {
    var parts = presented.Split(':', 2);
    if (parts.Length != 2 || !Guid.TryParse(parts[0], out var id) || parts[1].Length == 0)
    {
      return null;
    }

    if (await Tokens().FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } token)
    {
      return null;
    }

    var now = clock.GetUtcNow();
    if (token.ExpiresAt is { } expires && expires <= now)
    {
      return null;
    }

    var verified = hasher.VerifyHashedPassword(token, token.HashedSecret, parts[1]);
    if (verified == PasswordVerificationResult.Failed)
    {
      return null;
    }

    if (verified == PasswordVerificationResult.SuccessRehashNeeded)
    {
      token.HashedSecret = hasher.HashPassword(token, parts[1]);
    }

    token.LastUsed = now;
    await db.SaveChangesAsync(cancellationToken);
    return token;
  }

  private IQueryable<PersonalAccessTokenRecord> Tokens() => db.PersonalAccessTokens.IgnoreQueryFilters();

  private Task<int> GrantCountAsync(Guid id, CancellationToken cancellationToken) =>
    db.PermissionAssignments.IgnoreQueryFilters().CountAsync(x => x.PrincipalKind == PermissionPrincipalKind.PersonalAccessToken && x.PrincipalId == id, cancellationToken);

  private static string? NameRefusal(string? name) =>
    string.IsNullOrWhiteSpace(name) || name.Trim().Length > CreatePersonalAccessTokenRequest.NameMax
      ? $"Enter a name of at most {CreatePersonalAccessTokenRequest.NameMax} characters."
      : null;

  private static IResult Invalid(string reason) => Results.Problem(reason, statusCode: StatusCodes.Status400BadRequest);

  private static PersonalAccessTokenSummary Summary(PersonalAccessTokenRecord x, int grants) =>
    new(x.Id, x.UserId, x.Name, x.Mode, x.CreatedAt, x.ExpiresAt, x.LastUsed, grants);
}
