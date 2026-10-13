using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Permissions;

namespace StealthDesk.Web.Client.Permissions;

/// <summary>What a permission call answered: the value, or why not.</summary>
public sealed record PermissionResult<T>(T? Value, string? Error = null, bool SessionExpired = false)
{
  public bool Succeeded => Error is null && !SessionExpired;
}

/// <summary>Permission assignments, the catalog and presets, and effective permission checks.</summary>
public sealed class PermissionApi(HttpClient http)
{
  public Task<PermissionResult<PermissionCatalogList>> CatalogAsync() =>
    SendAsync<PermissionCatalogList>(HttpMethod.Get, Routes.PermissionCatalog);

  public Task<PermissionResult<PermissionPresetList>> PresetsAsync() =>
    SendAsync<PermissionPresetList>(HttpMethod.Get, Routes.PermissionPresets);

  public Task<PermissionResult<PermissionAssignmentList>> ListAsync(Guid tenantId, PermissionPrincipalKind kind, Guid principalId) =>
    SendAsync<PermissionAssignmentList>(HttpMethod.Get, $"{Routes.PermissionAssignments}?tenantId={tenantId}&principalKind={kind}&principalId={principalId}");

  public Task<PermissionResult<PermissionAssignment>> CreateAsync(Guid tenantId, CreatePermissionAssignmentRequest request) =>
    SendAsync<PermissionAssignment>(HttpMethod.Post, $"{Routes.PermissionAssignments}?tenantId={tenantId}", request);

  public Task<PermissionResult<PermissionAssignment>> UpdateAsync(Guid tenantId, Guid id, UpdatePermissionAssignmentRequest request) =>
    SendAsync<PermissionAssignment>(HttpMethod.Put, $"{Routes.PermissionAssignment(id)}?tenantId={tenantId}", request);

  public Task<PermissionResult<DeletePermissionAssignmentsResult>> DeleteAsync(Guid tenantId, IReadOnlyList<Guid> ids) =>
    SendAsync<DeletePermissionAssignmentsResult>(HttpMethod.Post, $"{Routes.PermissionAssignmentsBatchDelete}?tenantId={tenantId}", new DeletePermissionAssignmentsRequest(ids));

  public Task<PermissionResult<int>> ApplyPresetsAsync(Guid tenantId, ApplyPermissionPresetsRequest request) =>
    SendAsync<int>(HttpMethod.Post, $"{Routes.ApplyPermissionPresets}?tenantId={tenantId}", request);

  public Task<PermissionResult<EffectivePermission>> CheckAsync(
    Guid tenantId,
    PermissionPrincipalKind kind,
    Guid principalId,
    string permission,
    PermissionScopeKind scope,
    Guid? scopeId) =>
    SendAsync<EffectivePermission>(
      HttpMethod.Get,
      $"{Routes.EffectivePermission(principalId)}?tenantId={tenantId}&principalKind={kind}&permission={Uri.EscapeDataString(permission)}&scopeKind={scope}"
        + (scopeId is { } id ? $"&scopeId={id}" : string.Empty));

  private async Task<PermissionResult<T>> SendAsync<T>(HttpMethod method, string path, object? body = null)
  {
    try
    {
      using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
      using var response = await http.SendAsync(request);
      if (response.IsSuccessStatusCode)
      {
        return new PermissionResult<T>(response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<T>());
      }

      return response.StatusCode switch
      {
        HttpStatusCode.Unauthorized => new PermissionResult<T>(default, "Your session ended.", SessionExpired: true),
        HttpStatusCode.NotFound => new PermissionResult<T>(default, "It no longer exists."),
        _ => new PermissionResult<T>(default, await ProblemAsync(response)),
      };
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException)
    {
      return new PermissionResult<T>(default, "The server can't be reached. Try again in a moment.");
    }
  }

  // The server explains refusals: the right that's missing, a scope that doesn't fit, a lockout.
  private static async Task<string> ProblemAsync(HttpResponseMessage response)
  {
    try
    {
      var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
      if (problem.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } text)
      {
        return text;
      }
    }
    catch (JsonException)
    {
    }

    return response.StatusCode == HttpStatusCode.Forbidden ? "Your account can't do that." : "Something went wrong. Try again in a moment.";
  }
}
