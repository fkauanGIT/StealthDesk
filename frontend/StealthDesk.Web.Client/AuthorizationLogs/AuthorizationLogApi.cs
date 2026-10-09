using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using StealthDesk.Contracts;
using StealthDesk.Contracts.AuthorizationLogs;

namespace StealthDesk.Web.Client.AuthorizationLogs;

/// <summary>A page of entries, or why it couldn't be read.</summary>
public sealed record AuthorizationLogResult(AuthorizationChangePage? Page, string? Error, bool SessionExpired = false);

/// <summary>Reads the authorization change log.</summary>
public sealed class AuthorizationLogApi(HttpClient http)
{
  public async Task<AuthorizationLogResult> GetTenantAsync(Guid tenantId, AuthorizationChangeQuery query, CancellationToken cancellationToken = default)
  {
    try
    {
      using var response = await http.GetAsync($"{Routes.AuthorizationLogs}?{QueryString(query)}&tenantId={tenantId}", cancellationToken);
      return response.StatusCode switch
      {
        HttpStatusCode.OK => new AuthorizationLogResult(await response.Content.ReadFromJsonAsync<AuthorizationChangePage>(cancellationToken), null),
        HttpStatusCode.Unauthorized => new AuthorizationLogResult(null, "Your session ended.", SessionExpired: true),
        HttpStatusCode.Forbidden => new AuthorizationLogResult(null, "Your account can't read this log."),
        HttpStatusCode.BadRequest => new AuthorizationLogResult(null, "Check the filters and try again."),
        _ => new AuthorizationLogResult(null, "Could not load the log. Try again in a moment."),
      };
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException)
    {
      return new AuthorizationLogResult(null, "The server can't be reached. Try again in a moment.");
    }
  }

  public static string QueryString(AuthorizationChangeQuery query)
  {
    var parts = new List<string> { $"page={query.Page}", $"pageSize={query.PageSize}" };
    Add(parts, "action", query.Action);
    Add(parts, "actorKind", query.ActorKind);
    Add(parts, "targetKind", query.TargetKind);
    Add(parts, "search", query.Search?.Trim());
    Add(parts, "from", query.From?.ToString("O", CultureInfo.InvariantCulture));
    Add(parts, "to", query.To?.ToString("O", CultureInfo.InvariantCulture));
    return string.Join('&', parts);
  }

  private static void Add(List<string> parts, string name, string? value)
  {
    if (!string.IsNullOrWhiteSpace(value))
    {
      parts.Add($"{name}={Uri.EscapeDataString(value)}");
    }
  }
}

/// <summary>The values the filters offer, read from the contract so new actions and targets appear on their own.</summary>
public static class ChangeLogVocabulary
{
  public static IReadOnlyList<string> Actions { get; } = Constants(typeof(AuthorizationChangeActions));

  public static IReadOnlyList<string> Actors { get; } = Constants(typeof(AuthorizationChangeActors));

  public static IReadOnlyList<string> Targets { get; } = Constants(typeof(AuthorizationChangeTargets));

  private static List<string> Constants(Type type) =>
    [.. type.GetFields(BindingFlags.Public | BindingFlags.Static)
      .Where(x => x.IsLiteral && x.FieldType == typeof(string))
      .Select(x => (string)x.GetRawConstantValue()!)
      .Order()];
}
