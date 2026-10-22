namespace StealthDesk.Web.Server.Persistence;

/// <summary>One change to who can do what: what was done, by whom, to what, and how it looked before and after.</summary>
public class AuthorizationChangeRecord
{
  public const int ActionMax = 100;
  public const int ActorKindMax = 50;
  public const int TargetKindMax = 100;
  public const int IpAddressMax = 64;
  public const int CorrelationIdMax = 100;

  public Guid Id { get; set; }

  /// <summary>One of <see cref="Contracts.AuthorizationLogs.AuthorizationChangeActions"/>.</summary>
  public string Action { get; set; } = string.Empty;

  /// <summary>One of <see cref="Contracts.AuthorizationLogs.AuthorizationChangeActors"/>.</summary>
  public string ActorKind { get; set; } = string.Empty;

  /// <summary>None when the server made the change.</summary>
  public Guid? ActorId { get; set; }

  /// <summary>One of <see cref="Contracts.AuthorizationLogs.AuthorizationChangeTargets"/>.</summary>
  public string TargetKind { get; set; } = string.Empty;
  public Guid? TargetId { get; set; }

  /// <summary>The tenant the change belongs to; none for server-wide changes.</summary>
  public Guid? OwningTenantId { get; set; }

  public string? BeforeJson { get; set; }
  public string? AfterJson { get; set; }

  /// <summary>Where the request came from; none for changes made outside a request.</summary>
  public string? IpAddress { get; set; }

  /// <summary>The trace id of the request, to find its other logs.</summary>
  public string? CorrelationId { get; set; }

  public DateTimeOffset CreatedAt { get; set; }
}
