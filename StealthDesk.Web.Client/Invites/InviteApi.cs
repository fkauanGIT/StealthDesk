using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Invites;

namespace StealthDesk.Web.Client.Invites;

/// <summary>What an invite call answered: the invite or list, or why not.</summary>
public sealed record InviteResult<T>(T? Value, string? Error = null, bool SessionExpired = false)
{
  public bool Succeeded => Error is null && !SessionExpired;
}

/// <summary>The tenant's pending invites, as the invites page manages them.</summary>
public sealed class InviteApi(HttpClient http)
{
  private const string Unreachable = "The server can't be reached. Try again in a moment.";

  public Task<InviteResult<TenantInviteList>> ListAsync(Guid tenantId) =>
    SendAsync<TenantInviteList>(HttpMethod.Get, $"{Routes.Invites}?tenantId={tenantId}");

  public Task<InviteResult<TenantInvite>> CreateAsync(Guid tenantId, string email) =>
    SendAsync<TenantInvite>(HttpMethod.Post, $"{Routes.Invites}?tenantId={tenantId}", new CreateInviteRequest(email));

  public Task<InviteResult<bool>> DeleteAsync(Guid id, Guid tenantId) =>
    SendAsync<bool>(HttpMethod.Delete, $"{Routes.Invite(id)}?tenantId={tenantId}");

  private async Task<InviteResult<T>> SendAsync<T>(HttpMethod method, string path, object? body = null)
  {
    try
    {
      using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
      using var response = await http.SendAsync(request);
      if (response.IsSuccessStatusCode)
      {
        return new InviteResult<T>(response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<T>());
      }

      return response.StatusCode switch
      {
        HttpStatusCode.Unauthorized => new InviteResult<T>(default, "Your session ended.", SessionExpired: true),
        HttpStatusCode.Forbidden => new InviteResult<T>(default, "Your account can't do that."),
        HttpStatusCode.NotFound => new InviteResult<T>(default, "That invite no longer exists."),
        HttpStatusCode.Conflict or HttpStatusCode.BadRequest => new InviteResult<T>(default, await ProblemAsync(response)),
        _ => new InviteResult<T>(default, "Something went wrong. Try again in a moment."),
      };
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException)
    {
      return new InviteResult<T>(default, Unreachable);
    }
  }

  // The server says why: "already has an account", a field error, and so on.
  private static async Task<string> ProblemAsync(HttpResponseMessage response)
  {
    try
    {
      var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
      if (problem.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
      {
        return string.Join(" ", errors.EnumerateObject().SelectMany(x => x.Value.EnumerateArray()).Select(x => x.GetString()));
      }

      if (problem.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } text)
      {
        return text;
      }
    }
    catch (JsonException)
    {
    }

    return "Check the email address and try again.";
  }
}
