namespace StealthDesk.Contracts.Accounts;

/// <summary>What the sign-in pages need to know before anyone signs in.</summary>
public sealed record AccountSettings
{
  public bool RegistrationOpen { get; init; }
}
