using System.Security.Claims;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.Devices;

/// <summary>
/// The devices a principal may read, as a filter the database applies. Asking the evaluator device by device would
/// load every device first; the filter reads the rules the same way the evaluator does, so both always agree.
/// </summary>
public sealed class DeviceAccessScope
{
  public static DeviceAccessScope None { get; } = new(null, false, [], [], [], []);

  private readonly Guid? _tenantBoundary;
  private readonly bool _everyTenant;
  private readonly Guid[] _tenants;
  private readonly Guid[] _devices;
  private readonly Guid[] _deniedTenants;
  private readonly Guid[] _deniedDevices;

  private DeviceAccessScope(Guid? tenantBoundary, bool everyTenant, Guid[] tenants, Guid[] devices, Guid[] deniedTenants, Guid[] deniedDevices)
  {
    _tenantBoundary = tenantBoundary;
    _everyTenant = everyTenant;
    _tenants = tenants;
    _devices = devices;
    _deniedTenants = deniedTenants;
    _deniedDevices = deniedDevices;
  }

  public bool IsEmpty => !_everyTenant && _tenants.Length == 0 && _devices.Length == 0;

  /// <param name="tenantBoundary">The principal's tenant, which no device outside of is ever read; none for a server-wide principal.</param>
  public static DeviceAccessScope From(IEnumerable<PermissionRule> rules, Guid? tenantBoundary)
  {
    // device.read can be granted at every scope collected below, so an allow at any other scope reaches no device.
    var reading = rules.Where(x => x.Permission == PermissionNames.DeviceRead).ToList();
    var allows = reading.Where(x => x.Effect == PermissionEffect.Allow).ToList();
    var denies = reading.Where(x => x.Effect == PermissionEffect.Deny).ToList();
    if (allows.Count == 0 || denies.Any(x => x.ScopeKind == PermissionScopeKind.Server))
    {
      return None;
    }

    return new DeviceAccessScope(
      tenantBoundary,
      allows.Any(x => x.ScopeKind == PermissionScopeKind.Server),
      Ids(allows, PermissionScopeKind.Tenant),
      Ids(allows, PermissionScopeKind.Device),
      Ids(denies, PermissionScopeKind.Tenant),
      Ids(denies, PermissionScopeKind.Device));
  }

  public IQueryable<DeviceRecord> Apply(IQueryable<DeviceRecord> devices)
  {
    if (IsEmpty)
    {
      return devices.Where(_ => false);
    }

    var tenantBoundary = _tenantBoundary;
    var everyTenant = _everyTenant;
    var tenants = _tenants;
    var allowed = _devices;
    var deniedTenants = _deniedTenants;
    var deniedDevices = _deniedDevices;

    return devices.Where(x =>
      (tenantBoundary == null || x.TenantId == tenantBoundary)
      && (everyTenant || tenants.Contains(x.TenantId) || allowed.Contains(x.Id))
      && !deniedTenants.Contains(x.TenantId)
      && !deniedDevices.Contains(x.Id));
  }

  private static Guid[] Ids(IEnumerable<PermissionRule> rules, PermissionScopeKind kind) =>
    [.. rules.Where(x => x.ScopeKind == kind && x.ScopeId.HasValue).Select(x => x.ScopeId!.Value).Distinct()];
}

/// <summary>Finds the <see cref="DeviceAccessScope"/> of whoever is asking.</summary>
public interface IDeviceAccess
{
  Task<DeviceAccessScope> ForAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
}

public sealed class DeviceAccess(IPermissionEvaluator evaluator) : IDeviceAccess
{
  public async Task<DeviceAccessScope> ForAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
    Principal.From(user) is { } principal
      ? DeviceAccessScope.From(await evaluator.RulesAsync(principal, cancellationToken), principal.TenantId)
      : DeviceAccessScope.None;
}
