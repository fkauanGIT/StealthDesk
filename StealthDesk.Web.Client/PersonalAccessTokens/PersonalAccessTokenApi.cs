using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StealthDesk.Contracts;
using StealthDesk.Contracts.PersonalAccessTokens;

namespace StealthDesk.Web.Client.PersonalAccessTokens;

/// <summary>What a token call answered: the token or list, or why not.</summary>
public sealed record PersonalAccessTokenResult<T>(T? Value, string? Error = null, bool SessionExpired = false)
{
  public bool Succeeded => Error is null && !SessionExpired;
}

/// <summary>The signed-in user's own personal access tokens.</summary>
public sealed class PersonalAccessTokenApi(HttpClient http)
{
  private const string Unreachable = "The server can't be reached. Try again in a moment.";

  public Task<PersonalAccessTokenResult<PersonalAccessTokenList>> ListAsync() =>
    SendAsync<PersonalAccessTokenList>(HttpMethod.Get, Routes.PersonalAccessTokens);

  public Task<PersonalAccessTokenResult<CreatedPersonalAccessToken>> CreateAsync(CreatePersonalAccessTokenRequest request) =>
    SendAsync<CreatedPersonalAccessToken>(HttpMethod.Post, Routes.PersonalAccessTokens, request);

  public Task<PersonalAccessTokenResult<PersonalAccessTokenSummary>> RenameAsync(Guid id, string name) =>
    SendAsync<PersonalAccessTokenSummary>(HttpMethod.Put, Routes.PersonalAccessToken(id), new RenamePersonalAccessTokenRequest(name));

  public Task<PersonalAccessTokenResult<bool>> DeleteAsync(Guid id) =>
    SendAsync<bool>(HttpMethod.Delete, Routes.PersonalAccessToken(id));

  private async Task<PersonalAccessTokenResult<T>> SendAsync<T>(HttpMethod method, string path, object? body = null)
  {
    try
    {
      using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
      using var response = await http.SendAsync(request);
      if (response.IsSuccessStatusCode)
      {
        return new PersonalAccessTokenResult<T>(response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<T>());
      }

      return response.StatusCode switch
      {
        HttpStatusCode.Unauthorized => new PersonalAccessTokenResult<T>(default, "Your session ended.", SessionExpired: true),
        HttpStatusCode.Forbidden => new PersonalAccessTokenResult<T>(default, "Your account can't do that."),
        HttpStatusCode.NotFound => new PersonalAccessTokenResult<T>(default, "That token no longer exists."),
        HttpStatusCode.BadRequest => new PersonalAccessTokenResult<T>(default, await ProblemAsync(response)),
        _ => new PersonalAccessTokenResult<T>(default, "Something went wrong. Try again in a moment."),
      };
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException)
    {
      return new PersonalAccessTokenResult<T>(default, Unreachable);
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

    return "Check the token's details and try again.";
  }
}
