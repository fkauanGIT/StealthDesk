namespace StealthDesk.Contracts.Accounts;

/// <summary>Who is signed in, as the web client sees it.</summary>
public sealed record CurrentUser
{
  public Guid Id { get; init; }
  public string Email { get; init; } = string.Empty;
  public Guid TenantId { get; init; }
  public string TenantName { get; init; } = string.Empty;
  public bool EmailConfirmed { get; init; }
  public bool TwoFactorEnabled { get; init; }
  public bool MustChangePassword { get; init; }
  public bool IsServerAdministrator { get; init; }
  public bool IsTenantAdministrator { get; init; }

  /// <summary>
  /// The permissions the user holds on their tenant, or on the server for those that only exist there. They decide
  /// what the web client offers; the server still checks every request, and permissions on single devices or groups
  /// aren't listed.
  /// </summary>
  public IReadOnlyList<string> Permissions { get; init; } = [];
}
