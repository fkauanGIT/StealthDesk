using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StealthDesk.Contracts;
using StealthDesk.Contracts.TenantSettings;

namespace StealthDesk.Web.Client.TenantSettings;

/// <summary>What a settings call answered: the settings, or why not.</summary>
public sealed record TenantSettingResult<T>(T? Value, string? Error = null, bool SessionExpired = false)
{
  public bool Succeeded => Error is null && !SessionExpired;
}

/// <summary>The signed-in user's tenant settings, and the install options they resolve to.</summary>
public sealed class TenantSettingApi(HttpClient http)
{
  public Task<TenantSettingResult<TenantSettingList>> ListAsync() =>
    SendAsync<TenantSettingList>(HttpMethod.Get, Routes.TenantSettings);

  /// <summary>Saves several values at once; an empty one goes back to the server's default.</summary>
  public Task<TenantSettingResult<TenantSettingList>> SaveAsync(IReadOnlyDictionary<string, string?> values) =>
    SendAsync<TenantSettingList>(HttpMethod.Put, Routes.TenantSettings, new SetTenantSettingsRequest(values));

  public Task<TenantSettingResult<DeploymentOptions>> DeploymentOptionsAsync() =>
    SendAsync<DeploymentOptions>(HttpMethod.Get, Routes.DeploymentOptions);

  private async Task<TenantSettingResult<T>> SendAsync<T>(HttpMethod method, string path, object? body = null)
  {
    try
    {
      using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
      using var response = await http.SendAsync(request);
      if (response.IsSuccessStatusCode)
      {
        return new TenantSettingResult<T>(await response.Content.ReadFromJsonAsync<T>());
      }

      return response.StatusCode switch
      {
        HttpStatusCode.Unauthorized => new TenantSettingResult<T>(default, "Your session ended.", SessionExpired: true),
        HttpStatusCode.Forbidden => new TenantSettingResult<T>(default, "Your account can't do that."),
        HttpStatusCode.BadRequest => new TenantSettingResult<T>(default, await ProblemAsync(response)),
        HttpStatusCode.Conflict => new TenantSettingResult<T>(default, "The settings changed while saving. Try again."),
        _ => new TenantSettingResult<T>(default, "Something went wrong. Try again in a moment."),
      };
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException)
    {
      return new TenantSettingResult<T>(default, "The server can't be reached. Try again in a moment.");
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

    return "Check the values and try again.";
  }
}
