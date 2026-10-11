using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StealthDesk.Contracts;
using StealthDesk.Contracts.UserGroups;

namespace StealthDesk.Web.Client.UserGroups;

/// <summary>What a group call answered: the value, or why not.</summary>
public sealed record GroupResult<T>(T? Value, string? Error = null, bool SessionExpired = false, bool NotFound = false)
{
  public bool Succeeded => Error is null && !SessionExpired;
}

/// <summary>The tenant's user groups and their members, as the group pages manage them.</summary>
public sealed class UserGroupApi(HttpClient http)
{
  public Task<GroupResult<UserGroupList>> ListAsync(Guid tenantId) =>
    SendAsync<UserGroupList>(HttpMethod.Get, $"{Routes.UserGroups}?tenantId={tenantId}");

  public Task<GroupResult<UserGroupDetail>> GetAsync(Guid id, Guid tenantId) =>
    SendAsync<UserGroupDetail>(HttpMethod.Get, $"{Routes.UserGroup(id)}?tenantId={tenantId}");

  public Task<GroupResult<UserGroupDetail>> CreateAsync(Guid tenantId, UserGroupRequest request) =>
    SendAsync<UserGroupDetail>(HttpMethod.Post, $"{Routes.UserGroups}?tenantId={tenantId}", request);

  public Task<GroupResult<UserGroupDetail>> UpdateAsync(Guid id, Guid tenantId, UserGroupRequest request) =>
    SendAsync<UserGroupDetail>(HttpMethod.Put, $"{Routes.UserGroup(id)}?tenantId={tenantId}", request);

  public Task<GroupResult<bool>> DeleteAsync(Guid id, Guid tenantId) =>
    SendAsync<bool>(HttpMethod.Delete, $"{Routes.UserGroup(id)}?tenantId={tenantId}");

  public Task<GroupResult<bool>> AddMembersAsync(Guid id, Guid tenantId, IReadOnlyList<Guid> userIds) =>
    SendAsync<bool>(HttpMethod.Post, $"{Routes.UserGroupMembers(id)}?tenantId={tenantId}", new UserGroupMembersRequest(userIds));

  public Task<GroupResult<bool>> RemoveMembersAsync(Guid id, Guid tenantId, IReadOnlyList<Guid> userIds) =>
    SendAsync<bool>(HttpMethod.Delete, $"{Routes.UserGroupMembers(id)}?tenantId={tenantId}", new UserGroupMembersRequest(userIds));

  private async Task<GroupResult<T>> SendAsync<T>(HttpMethod method, string path, object? body = null)
  {
    try
    {
      using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
      using var response = await http.SendAsync(request);
      if (response.IsSuccessStatusCode)
      {
        return new GroupResult<T>(response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<T>());
      }

      return response.StatusCode switch
      {
        HttpStatusCode.Unauthorized => new GroupResult<T>(default, "Your session ended.", SessionExpired: true),
        HttpStatusCode.Forbidden => new GroupResult<T>(default, "Your account can't do that."),
        HttpStatusCode.NotFound => new GroupResult<T>(default, "That group no longer exists.", NotFound: true),
        HttpStatusCode.Conflict or HttpStatusCode.BadRequest => new GroupResult<T>(default, await ProblemAsync(response)),
        _ => new GroupResult<T>(default, "Something went wrong. Try again in a moment."),
      };
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException)
    {
      return new GroupResult<T>(default, "The server can't be reached. Try again in a moment.");
    }
  }

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

    return "Check the values and try again.";
  }
}
