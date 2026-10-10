namespace StealthDesk.Web.Server.Persistence;

/// <summary>
/// An invitation into a tenant. The invitee's account already exists in the tenant; whoever opens the link with the
/// right email chooses its password.
/// </summary>
public class TenantInviteRecord
{
  public const int ActivationCodeLength = 64;
  public const int EmailMax = 256;

  public Guid Id { get; set; }

  public Guid TenantId { get; set; }
  public TenantRecord? Tenant { get; set; }

  /// <summary>Trimmed and lower-cased, so the email typed when accepting matches however it was written.</summary>
  public string InviteeEmail { get; set; } = string.Empty;

  /// <summary>The secret in the invite link: whoever holds it, with the email, takes over the account.</summary>
  public string ActivationCode { get; set; } = string.Empty;

  public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
