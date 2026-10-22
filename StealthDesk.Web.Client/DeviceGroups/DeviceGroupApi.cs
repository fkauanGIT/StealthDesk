using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StealthDesk.Contracts;
using StealthDesk.Contracts.DeviceGroups;
using StealthDesk.Web.Client.UserGroups;

namespace StealthDesk.Web.Client.DeviceGroups;

/// <summary>The tenant's device groups and their devices, as the group pages manage them.</summary>
public sealed class DeviceGroupApi(HttpClient http)
{
  public Task<GroupResult<DeviceGroupList>> ListAsync(Guid tenantId) =>
    SendAsync<DeviceGroupList>(HttpMethod.Get, $"{Routes.DeviceGroups}?tenantId={tenantId}");

  public Task<GroupResult<DeviceGroupDetail>> GetAsync(Guid id, Guid tenantId) =>
    SendAsync<DeviceGroupDetail>(HttpMethod.Get, $"{Routes.DeviceGroup(id)}?tenantId={tenantId}");

  public Task<GroupResult<DeviceGroupDetail>> CreateAsync(Guid tenantId, DeviceGroupRequest request) =>
    SendAsync<DeviceGroupDetail>(HttpMethod.Post, $"{Routes.DeviceGroups}?tenantId={tenantId}", request);

  public Task<GroupResult<DeviceGroupDetail>> UpdateAsync(Guid id, Guid tenantId, DeviceGroupRequest request) =>
    SendAsync<DeviceGroupDetail>(HttpMethod.Put, $"{Routes.DeviceGroup(id)}?tenantId={tenantId}", request);

  public Task<GroupResult<bool>> DeleteAsync(Guid id, Guid tenantId) =>
    SendAsync<bool>(HttpMethod.Delete, $"{Routes.DeviceGroup(id)}?tenantId={tenantId}");

  /// <param name="fromGroupId">The group they move from, if they move.</param>
  public Task<GroupResult<bool>> AddDevicesAsync(Guid id, Guid tenantId, IReadOnlyList<Guid> deviceIds, Guid? fromGroupId = null) =>
    SendAsync<bool>(HttpMethod.Post, $"{Routes.DeviceGroupMembers(id)}?tenantId={tenantId}", new DeviceGroupMembersRequest(deviceIds, fromGroupId));

  public Task<GroupResult<bool>> RemoveDevicesAsync(Guid id, Guid tenantId, IReadOnlyList<Guid> deviceIds) =>
    SendAsync<bool>(HttpMethod.Delete, $"{Routes.DeviceGroupMembers(id)}?tenantId={tenantId}", new DeviceGroupMembersRequest(deviceIds));

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
