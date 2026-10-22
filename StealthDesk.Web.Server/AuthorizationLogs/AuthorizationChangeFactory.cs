using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.AuthorizationLogs;

/// <summary>
/// Builds change log entries. It never saves them: the caller adds the entry to the same context as the change, so
/// both are saved together or neither is.
/// </summary>
public interface IAuthorizationChangeFactory
{
  /// <param name="actor">Who made the change; null when the server did.</param>
  /// <param name="before">A snapshot of the target before the change, serialized as JSON.</param>
  /// <param name="after">A snapshot of the target after the change, serialized as JSON.</param>
  AuthorizationChangeRecord Create(
    string action,
    Principal? actor,
    string targetKind,
    Guid? targetId,
    Guid? owningTenantId,
    object? before = null,
    object? after = null);
}

public sealed class AuthorizationChangeFactory(IHttpContextAccessor http, TimeProvider clock) : IAuthorizationChangeFactory
{
  private static readonly JsonSerializerOptions Json = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter() },
  };

  public AuthorizationChangeRecord Create(
    string action,
    Principal? actor,
    string targetKind,
    Guid? targetId,
    Guid? owningTenantId,
    object? before = null,
    object? after = null) => new()
  {
    Action = action,
    ActorKind = ActorKind(actor),
    ActorId = NullIfEmpty(actor?.Id),
    TargetKind = targetKind,
    TargetId = NullIfEmpty(targetId),
    OwningTenantId = owningTenantId,
    BeforeJson = before is null ? null : JsonSerializer.Serialize(before, Json),
    AfterJson = after is null ? null : JsonSerializer.Serialize(after, Json),
    IpAddress = IpAddress(),
    CorrelationId = Activity.Current?.TraceId is { } trace && trace != default ? trace.ToString() : null,
    CreatedAt = clock.GetUtcNow(),
  };

  private static string ActorKind(Principal? actor) => actor?.Kind switch
  {
    null => AuthorizationChangeActors.System,
    PermissionPrincipalKind.ServiceAccount => AuthorizationChangeActors.ServiceAccount,
    _ => AuthorizationChangeActors.User,
  };

  // An id not saved yet is empty; writing it would point the entry at nothing.
  private static Guid? NullIfEmpty(Guid? id) => id == Guid.Empty ? null : id;

  // Outside a request (startup, background work) there is no address.
  private string? IpAddress()
  {
    if (http.HttpContext?.Connection.RemoteIpAddress is not { } address)
    {
      return null;
    }

    var text = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    return text.Length <= AuthorizationChangeRecord.IpAddressMax ? text : text[..AuthorizationChangeRecord.IpAddressMax];
  }
}
