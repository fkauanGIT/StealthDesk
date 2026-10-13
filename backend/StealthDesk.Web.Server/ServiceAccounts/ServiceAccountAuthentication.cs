using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.ServiceAccounts;
using StealthDesk.Web.Server.Accounts;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.ServiceAccounts;

/// <summary>
/// Signs a request in as the service account whose credential is in its header. A tenant account carries its
/// tenant; a server account none. Repeated failures are refused for a while, counted per address and per
/// credential, so neither guessing from one place nor guessing one credential from many places gets far.
/// </summary>
public sealed class ServiceAccountAuthentication(
  IOptionsMonitor<AuthenticationSchemeOptions> options,
  ILoggerFactory logger,
  UrlEncoder encoder,
  ServiceAccountService accounts,
  IOptionsMonitor<ServiceAccountOptions> limits,
  IMemoryCache failures) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
  public const string SchemeName = "ServiceAccountCredential";

  protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
  {
    var presented = Request.Headers[ServiceAccountHeader.Name].ToString().Trim();
    if (presented.Length == 0)
    {
      return AuthenticateResult.NoResult();
    }

    // Only a well-formed credential id gets its own counter; anything else shares one, so made-up values can't fill the cache.
    var address = $"sa-failures:address:{Context.Connection.RemoteIpAddress}";
    var credential = $"sa-failures:credential:{(Guid.TryParse(presented.Split(':', 2)[0], out var credentialId) ? credentialId : "invalid")}";
    var byAddress = failures.Get<int>(address);
    var byCredential = failures.Get<int>(credential);
    var limit = limits.CurrentValue.AuthFailureLimit;
    if (byAddress >= limit || byCredential >= limit)
    {
      return AuthenticateResult.Fail("Too many failed attempts. Try again later.");
    }

    if (await accounts.ValidateAsync(presented, Context.RequestAborted) is not { } valid)
    {
      var window = TimeSpan.FromMinutes(limits.CurrentValue.AuthFailureWindowMinutes);
      failures.Set(address, byAddress + 1, window);
      failures.Set(credential, byCredential + 1, window);
      return AuthenticateResult.Fail("Invalid service account credential.");
    }

    failures.Remove(address);
    failures.Remove(credential);

    var claims = new List<Claim>
    {
      new(ClaimTypes.Name, valid.AccountName),
      new(Principal.KindClaim, nameof(PermissionPrincipalKind.ServiceAccount)),
      new(Principal.IdClaim, valid.AccountId.ToString()),
    };
    if (valid.TenantId is { } tenant)
    {
      claims.Add(new Claim(StealthDeskClaims.TenantId, tenant.ToString()));
    }

    return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
  }
}
