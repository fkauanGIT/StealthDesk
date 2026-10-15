using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StealthDesk.Contracts;
using StealthDesk.Contracts.ServerSettings;
using StealthDesk.Contracts.Tenants;

namespace StealthDesk.Web.Client.ServerAdministration;

/// <summary>What a server administration call answered, or why not.</summary>
public sealed record ServerAdministrationResult<T>(T? Value, string? Error = null, bool SessionExpired = false)
{
  public bool Succeeded => Error is null && !SessionExpired;
}

/// <summary>The alert the layout shows, if any; kept apart so pages can be tested without it.</summary>
public interface IServerAlerts
{
  Task<ServerAlert?> CurrentAsync();
}

public sealed class ServerAlerts(ServerAdministrationApi api) : IServerAlerts
{
  public async Task<ServerAlert?> CurrentAsync() => (await api.AlertAsync()).Value;
}

/// <summary>The server's tenants, its alert and its test email.</summary>
public sealed class ServerAdministrationApi(HttpClient http)
{
  public Task<ServerAdministrationResult<TenantList>> TenantsAsync() =>
    SendAsync<TenantList>(HttpMethod.Get, Routes.Tenants);

  public Task<ServerAdministrationResult<TenantSummary>> CreateTenantAsync(string name) =>
    SendAsync<TenantSummary>(HttpMethod.Post, Routes.Tenants, new CreateTenantRequest(name));

  public Task<ServerAdministrationResult<TenantSummary>> RenameTenantAsync(Guid id, string name) =>
    SendAsync<TenantSummary>(HttpMethod.Put, Routes.Tenant(id), new RenameTenantRequest(name));

  public Task<ServerAdministrationResult<bool>> DeleteTenantAsync(Guid id) =>
    SendAsync<bool>(HttpMethod.Delete, Routes.Tenant(id));

  /// <summary>The alert, or none when it was never set.</summary>
  public Task<ServerAdministrationResult<ServerAlert>> AlertAsync() =>
    SendAsync<ServerAlert>(HttpMethod.Get, Routes.ServerAlert);

  public Task<ServerAdministrationResult<ServerAlert>> SaveAlertAsync(ServerAlert alert) =>
    SendAsync<ServerAlert>(HttpMethod.Put, Routes.ServerAlert, alert);

  public Task<ServerAdministrationResult<TestEmailResult>> SendTestEmailAsync() =>
    SendAsync<TestEmailResult>(HttpMethod.Post, Routes.ServerTestEmail);

  private async Task<ServerAdministrationResult<T>> SendAsync<T>(HttpMethod method, string path, object? body = null)
  {
    try
    {
      using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
      using var response = await http.SendAsync(request);
      if (response.IsSuccessStatusCode)
      {
        return new ServerAdministrationResult<T>(response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<T>());
      }

      return response.StatusCode switch
      {
        HttpStatusCode.Unauthorized => new ServerAdministrationResult<T>(default, "Your session ended.", SessionExpired: true),
        HttpStatusCode.Forbidden => new ServerAdministrationResult<T>(default, "Your account can't do that."),
        HttpStatusCode.NotFound => new ServerAdministrationResult<T>(default, "That tenant no longer exists."),
        HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.BadGateway =>
          new ServerAdministrationResult<T>(default, await ProblemAsync(response)),
        _ => new ServerAdministrationResult<T>(default, "Something went wrong. Try again in a moment."),
      };
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException)
    {
      return new ServerAdministrationResult<T>(default, "The server can't be reached. Try again in a moment.");
    }
  }

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

    return "Check the details and try again.";
  }
}
