namespace StealthDesk.Contracts.AuthorizationLogs;

/// <summary>One change log entry. Before and after are the snapshots as recorded, in JSON.</summary>
public sealed record AuthorizationChangeEntry(
  Guid Id,
  string Action,
  string ActorKind,
  Guid? ActorId,
  string TargetKind,
  Guid? TargetId,
  Guid? OwningTenantId,
  string? IpAddress,
  DateTimeOffset CreatedAt,
  string? BeforeJson,
  string? AfterJson);

/// <summary>Filters and the page to read, newest entries first. Every filter is optional.</summary>
public sealed class AuthorizationChangeQuery
{
  public const int MaxPageSize = 100;
  public const int SearchMax = 100;

  public string? Action { get; set; }

  public string? ActorKind { get; set; }

  public string? TargetKind { get; set; }

  /// <summary>An actor or target id, whole or in part.</summary>
  public string? Search { get; set; }

  public DateTimeOffset? From { get; set; }

  public DateTimeOffset? To { get; set; }

  /// <summary>Zero-based.</summary>
  public int Page { get; set; }

  public int PageSize { get; set; } = 50;
}

public sealed class AuthorizationChangePage
{
  public IReadOnlyList<AuthorizationChangeEntry> Items { get; init; } = [];

  /// <summary>Entries matching the filters, across every page.</summary>
  public int TotalItems { get; init; }
}
