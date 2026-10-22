using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Users;

namespace StealthDesk.Web.Client.Users;

/// <summary>What a user call answered: success, or why not.</summary>
public sealed record UserResult(bool Succeeded, string? Error = null, bool SessionExpired = false)
{
  public static readonly UserResult Success = new(true);
}

/// <summary>The tenant's users, as the users page reads and changes them.</summary>
public sealed class UserApi(HttpClient http)
{
  private const string Unreachable = "The server can't be reached. Try again in a moment.";

  public async Task<(TenantUserList? List, UserResult Result)> ListAsync(Guid tenantId, CancellationToken cancellationToken = default)
  {
    try
    {
      using var response = await http.GetAsync($"{Routes.Users}?tenantId={tenantId}", cancellationToken);
      return response.StatusCode == HttpStatusCode.OK
        ? (await response.Content.ReadFromJsonAsync<TenantUserList>(cancellationToken), UserResult.Success)
        : (null, Failure(response.StatusCode));
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException)
    {
      return (null, new UserResult(false, Unreachable));
    }
  }

  public async Task<UserResult> DeleteAsync(Guid id, Guid tenantId, CancellationToken cancellationToken = default)
  {
    try
    {
      using var response = await http.DeleteAsync($"{Routes.User(id)}?tenantId={tenantId}", cancellationToken);
      return response.IsSuccessStatusCode ? UserResult.Success : Failure(response.StatusCode);
    }
    catch (HttpRequestException)
    {
      return new UserResult(false, Unreachable);
    }
  }

  private static UserResult Failure(HttpStatusCode status) => status switch
  {
    HttpStatusCode.Unauthorized => new UserResult(false, "Your session ended.", SessionExpired: true),
    HttpStatusCode.Forbidden => new UserResult(false, "Your account can't do that."),
    HttpStatusCode.NotFound => new UserResult(false, "That user no longer exists."),
    HttpStatusCode.BadRequest => new UserResult(false, "You can't delete your own account here; use your account settings."),
    _ => new UserResult(false, "Something went wrong. Try again in a moment."),
  };
}
