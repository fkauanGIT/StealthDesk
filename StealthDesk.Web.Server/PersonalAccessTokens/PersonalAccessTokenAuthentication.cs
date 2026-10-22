using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using StealthDesk.Contracts.PersonalAccessTokens;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.Accounts;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.PersonalAccessTokens;

/// <summary>
/// Signs a request in as the owner of the personal access token in its header, marked as acting through that token.
/// Repeated failures are refused for a while, counted per address and per token, so neither guessing secrets from one
/// place nor guessing one token from many places gets far.
/// </summary>
public sealed class PersonalAccessTokenAuthentication(
  IOptionsMonitor<AuthenticationSchemeOptions> options,
  ILoggerFactory logger,
  UrlEncoder encoder,
  PersonalAccessTokenService tokens,
  UserManager<UserRecord> users,
  IMemoryCache failures) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
  public const string SchemeName = "PersonalAccessToken";
  public const int MaxFailures = 5;
  public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);

  protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
  {
    var presented = Request.Headers[PersonalAccessTokenHeader.Name].ToString().Trim();
    if (presented.Length == 0)
    {
      return AuthenticateResult.NoResult();
    }

    // Only a well-formed token id gets its own counter; anything else shares one, so made-up values can't fill the cache.
    var address = $"pat-failures:address:{Context.Connection.RemoteIpAddress}";
    var token = $"pat-failures:token:{(Guid.TryParse(presented.Split(':', 2)[0], out var tokenId) ? tokenId : "invalid")}";
    var byAddress = failures.Get<int>(address);
    var byToken = failures.Get<int>(token);
    if (byAddress >= MaxFailures || byToken >= MaxFailures)
    {
      return AuthenticateResult.Fail("Too many failed attempts. Try again later.");
    }

    if (await tokens.ValidateAsync(presented, Context.RequestAborted) is not { } valid
      || await users.FindByIdAsync(valid.UserId.ToString()) is not { } user
      || await users.IsLockedOutAsync(user))
    {
      failures.Set(address, byAddress + 1, FailureWindow);
      failures.Set(token, byToken + 1, FailureWindow);
      return AuthenticateResult.Fail("Invalid personal access token.");
    }

    failures.Remove(address);
    failures.Remove(token);

    var claims = new List<Claim>
    {
      new(ClaimTypes.NameIdentifier, user.Id.ToString()),
      new(ClaimTypes.Name, user.UserName ?? user.Id.ToString()),
      new(StealthDeskClaims.TenantId, user.TenantId.ToString()),
      new(Principal.KindClaim, nameof(PermissionPrincipalKind.User)),
      new(Principal.IdClaim, user.Id.ToString()),
      new(Principal.TokenClaim, valid.Id.ToString()),
    };
    if (user.Email is { } email)
    {
      claims.Add(new Claim(ClaimTypes.Email, email));
    }

    return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
  }
}
