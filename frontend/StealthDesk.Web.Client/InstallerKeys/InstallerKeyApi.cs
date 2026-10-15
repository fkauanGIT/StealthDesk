using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StealthDesk.Contracts;
using StealthDesk.Contracts.InstallerKeys;

namespace StealthDesk.Web.Client.InstallerKeys;

/// <summary>What an installer key call answered: the key or list, or why not.</summary>
public sealed record InstallerKeyResult<T>(T? Value, string? Error = null, bool SessionExpired = false)
{
  public bool Succeeded => Error is null && !SessionExpired;
}

/// <summary>The installer keys of the signed-in user's tenant.</summary>
public sealed class InstallerKeyApi(HttpClient http)
{
  private const string Unreachable = "The server can't be reached. Try again in a moment.";

  public Task<InstallerKeyResult<InstallerKeyList>> ListAsync() =>
    SendAsync<InstallerKeyList>(HttpMethod.Get, Routes.InstallerKeys);

  public Task<InstallerKeyResult<CreatedInstallerKey>> CreateAsync(CreateInstallerKeyRequest request) =>
    SendAsync<CreatedInstallerKey>(HttpMethod.Post, Routes.InstallerKeys, request);

  public Task<InstallerKeyResult<bool>> RenameAsync(Guid id, string? name) =>
    SendAsync<bool>(HttpMethod.Put, Routes.InstallerKey(id), new RenameInstallerKeyRequest(name));

  public Task<InstallerKeyResult<bool>> DeleteAsync(Guid id) =>
    SendAsync<bool>(HttpMethod.Delete, Routes.InstallerKey(id));

  public Task<InstallerKeyResult<InstallerKeyUsageList>> UsagesAsync(Guid id) =>
    SendAsync<InstallerKeyUsageList>(HttpMethod.Get, Routes.InstallerKeyUsages(id));

  private async Task<InstallerKeyResult<T>> SendAsync<T>(HttpMethod method, string path, object? body = null)
  {
    try
    {
      using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
      using var response = await http.SendAsync(request);
      if (response.IsSuccessStatusCode)
      {
        return new InstallerKeyResult<T>(response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<T>());
      }

      return response.StatusCode switch
      {
        HttpStatusCode.Unauthorized => new InstallerKeyResult<T>(default, "Your session ended.", SessionExpired: true),
        HttpStatusCode.Forbidden => new InstallerKeyResult<T>(default, "Your account can't do that."),
        HttpStatusCode.NotFound => new InstallerKeyResult<T>(default, "That key no longer exists."),
        HttpStatusCode.BadRequest => new InstallerKeyResult<T>(default, await ProblemAsync(response)),
        _ => new InstallerKeyResult<T>(default, "Something went wrong. Try again in a moment."),
      };
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException)
    {
      return new InstallerKeyResult<T>(default, Unreachable);
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

    return "Check the key's details and try again.";
  }
}

/// <summary>How keys are described on the pages.</summary>
public static class InstallerKeyText
{
  public static string Type(InstallerKeyType type) => type switch
  {
    InstallerKeyType.UsageBased => "Usage-based",
    InstallerKeyType.TimeBased => "Time-based",
    _ => "Persistent",
  };

  /// <summary>The command that installs the agent with this key, for the server the page was opened from.</summary>
  /// <param name="instance">The agent instance the tenant's settings name, if any.</param>
  public static string AgentCommand(Uri server, string fullKey, Guid? deviceId, string? instance = null) =>
    WithOptions($".\\StealthDesk.Agent.exe run --server {server.GetLeftPart(UriPartial.Authority)} --installer-key {fullKey}", deviceId, instance);

  /// <summary>The same, for an agent run from the repository; development agents already point at the local server.</summary>
  public static string SourceCommand(string fullKey, Guid? deviceId, string? instance = null) =>
    WithOptions($"dotnet run --project agent/StealthDesk.Agent -- run --installer-key {fullKey}", deviceId, instance);

  private static string WithOptions(string command, Guid? deviceId, string? instance)
  {
    if (deviceId is { } id && id != Guid.Empty)
    {
      command += $" --device-id {id}";
    }

    return string.IsNullOrWhiteSpace(instance) ? command : $"{command} --instance {instance}";
  }
}
