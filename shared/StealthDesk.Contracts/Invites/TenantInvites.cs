namespace StealthDesk.Contracts.Invites;

/// <param name="InviteUrl">The link to share. Without the right to manage users it leads to the accept page without the code.</param>
public sealed record TenantInvite(Guid Id, DateTimeOffset CreatedAt, string InviteeEmail, string InviteUrl);

public sealed class TenantInviteList
{
  public IReadOnlyList<TenantInvite> Items { get; init; } = [];
}

public sealed record CreateInviteRequest(string InviteeEmail);

/// <summary>Sent from the invite link: the code it carries, the invited email and the password the person chooses.</summary>
public sealed record AcceptInviteRequest(string ActivationCode, string Email, string Password);
