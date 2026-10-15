namespace StealthDesk.Contracts.Tenants;

/// <summary>A tenant as the server's administrators see it.</summary>
public sealed record TenantSummary(Guid Id, string Name, DateTimeOffset CreatedAt, int UserCount, int DeviceCount);

public sealed class TenantList
{
  public IReadOnlyList<TenantSummary> Items { get; init; } = [];
}

public sealed record CreateTenantRequest(string Name)
{
  public const int NameMax = 100;
}

public sealed record RenameTenantRequest(string Name);
