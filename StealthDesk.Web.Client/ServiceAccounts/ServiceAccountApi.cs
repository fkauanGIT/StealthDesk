using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StealthDesk.Contracts;
using StealthDesk.Contracts.ServiceAccounts;

namespace StealthDesk.Web.Client.ServiceAccounts;

/// <summary>What a service account call answered: the account, list or key, or why not.</summary>
public sealed record ServiceAccountResult<T>(T? Value, string? Error = null, bool SessionExpired = false)
{
  public bool Succeeded => Error is null && !SessionExpired;
}

/// <summary>
/// A tenant's service accounts, or the server's when no tenant is given, and their credentials.
/// </summary>
public sealed class ServiceAccountApi(HttpClient http)
{
  private const string Unreachable = "The server can't be reached. Try again in a moment.";

  public Task<ServiceAccountResult<ServiceAccountList>> ListAsync(Guid? tenantId) =>
    SendAsync<ServiceAccountList>(HttpMethod.Get, Base(tenantId) + Query(tenantId));

  public Task<ServiceAccountResult<ServiceAccount>> CreateAsync(Guid? tenantId, CreateServiceAccountRequest request) =>
    SendAsync<ServiceAccount>(HttpMethod.Post, Base(tenantId) + Query(tenantId), request);

  public Task<ServiceAccountResult<ServiceAccount>> UpdateAsync(Guid? tenantId, Guid id, UpdateServiceAccountRequest request) =>
    SendAsync<ServiceAccount>(HttpMethod.Put, Routes.ServiceAccount(Base(tenantId), id) + Query(tenantId), request);

  public Task<ServiceAccountResult<bool>> DeleteAsync(Guid? tenantId, Guid id) =>
    SendAsync<bool>(HttpMethod.Delete, Routes.ServiceAccount(Base(tenantId), id) + Query(tenantId));

  public Task<ServiceAccountResult<CreatedServiceAccountCredential>> AddCredentialAsync(Guid? tenantId, Guid id, CreateServiceAccountCredentialRequest request) =>
    SendAsync<CreatedServiceAccountCredential>(HttpMethod.Post, Routes.ServiceAccountCredentials(Base(tenantId), id) + Query(tenantId), request);

  public Task<ServiceAccountResult<bool>> RevokeCredentialAsync(Guid? tenantId, Guid id, Guid credentialId) =>
    SendAsync<bool>(HttpMethod.Delete, Routes.ServiceAccountCredential(Base(tenantId), id, credentialId) + Query(tenantId));

  public Task<ServiceAccountResult<bool>> PurgeCredentialAsync(Guid? tenantId, Guid id, Guid credentialId) =>
    SendAsync<bool>(HttpMethod.Delete, Routes.ServiceAccountCredentialPurge(Base(tenantId), id, credentialId) + Query(tenantId));

  private static string Base(Guid? tenantId) => tenantId is null ? Routes.ServerServiceAccounts : Routes.ServiceAccounts;

  private static string Query(Guid? tenantId) => tenantId is { } id ? $"?tenantId={id}" : string.Empty;

  private async Task<ServiceAccountResult<T>> SendAsync<T>(HttpMethod method, string path, object? body = null)
  {
    try
    {
      using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
      using var response = await http.SendAsync(request);
      if (response.IsSuccessStatusCode)
      {
        return new ServiceAccountResult<T>(response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<T>());
      }

      return response.StatusCode switch
      {
        HttpStatusCode.Unauthorized => new ServiceAccountResult<T>(default, "Your session ended.", SessionExpired: true),
        HttpStatusCode.NotFound => new ServiceAccountResult<T>(default, "That service account or credential no longer exists."),
        HttpStatusCode.Forbidden or HttpStatusCode.BadRequest or HttpStatusCode.Conflict =>
          new ServiceAccountResult<T>(default, await ProblemAsync(response) ?? (response.StatusCode == HttpStatusCode.Forbidden ? "Your account can't do that." : "Check the details and try again.")),
        _ => new ServiceAccountResult<T>(default, "Something went wrong. Try again in a moment."),
      };
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException)
    {
      return new ServiceAccountResult<T>(default, Unreachable);
    }
  }

  // The server says why: a taken name, a disabled account, a credential still in use.
  private static async Task<string?> ProblemAsync(HttpResponseMessage response)
  {
    try
    {
      var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
      return problem.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } text ? text : null;
    }
    catch (JsonException)
    {
      return null;
    }
  }
}
