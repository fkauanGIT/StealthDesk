namespace StealthDesk.Contracts.Users;

/// <summary>A user of the tenant, with the permissions granted to them directly.</summary>
public sealed record TenantUser(Guid Id, string UserName, string? Email, DateTimeOffset CreatedAt, IReadOnlyList<string> Permissions);

public sealed class TenantUserList
{
  public IReadOnlyList<TenantUser> Items { get; init; } = [];
}

/// <param name="Email">Defaults to <paramref name="UserName"/>.</param>
/// <param name="Password">Without one, the user signs in another way or has it reset.</param>
/// <param name="Presets">Granted on top of the baseline every user holds.</param>
public sealed record CreateUserRequest(string UserName, string? Email = null, string? Password = null, IReadOnlyList<string>? Presets = null);

/// <summary>A password set by an administrator, to hand to the user; it must be changed at the next sign-in.</summary>
public sealed record TemporaryPassword(string Password);
