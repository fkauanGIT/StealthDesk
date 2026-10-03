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
}
