namespace StealthDesk.Web.Server.AuthorizationLogs;

/// <summary>
/// Presets granted in one go, e.g. at registration: one entry for the whole grant instead of one per permission.
/// </summary>
public sealed record PermissionSeedSummary(int Count, IReadOnlyList<string> Presets);
