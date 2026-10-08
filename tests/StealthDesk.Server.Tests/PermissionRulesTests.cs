using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Server.Tests;

/// <summary>The decision rules, without a database: default deny, deny wins, scopes and the tenant boundary.</summary>
public class PermissionRulesTests
{
  private static readonly Guid Tenant = Guid.NewGuid();
  private static readonly Guid OtherTenant = Guid.NewGuid();
  private static readonly Guid Device = Guid.NewGuid();

  private static PermissionRule Rule(
    string permission,
    PermissionScopeKind scope,
    Guid? scopeId,
    PermissionEffect effect = PermissionEffect.Allow,
    RuleSource source = RuleSource.Direct) =>
    new(permission, effect, scope, scopeId, scope == PermissionScopeKind.Server ? null : Tenant, source);

  [Fact]
  public void NoAssignment_IsDenied()
  {
    var decision = PermissionRules.Evaluate([], PermissionNames.DeviceRead, Resource.Device(Device, Tenant));

    Assert.False(decision.Allowed);
    Assert.Equal("No assignment grants it.", decision.Reason);
  }

  [Fact]
  public void UnknownPermission_IsDenied_EvenWithAMatchingAssignment()
  {
    var decision = PermissionRules.Evaluate([Rule("device.teleport", PermissionScopeKind.Server, null)], "device.teleport", Resource.Server);

    Assert.False(decision.Allowed);
    Assert.Contains("Unknown permission", decision.Reason);
  }

  [Fact]
  public void DirectDeny_WinsOverAnAllowFromAGroup()
  {
    var rules = new[]
    {
      Rule(PermissionNames.DeviceRead, PermissionScopeKind.Tenant, Tenant, source: RuleSource.UserGroup),
      Rule(PermissionNames.DeviceRead, PermissionScopeKind.Device, Device, PermissionEffect.Deny),
    };

    var decision = PermissionRules.Evaluate(rules, PermissionNames.DeviceRead, Resource.Device(Device, Tenant));

    Assert.False(decision.Allowed);
    Assert.Equal("Denied by a direct assignment at Device scope.", decision.Reason);
  }

  [Fact]
  public void GroupDeny_WinsOverADirectAllow()
  {
    var rules = new[]
    {
      Rule(PermissionNames.DeviceRead, PermissionScopeKind.Tenant, Tenant),
      Rule(PermissionNames.DeviceRead, PermissionScopeKind.Tenant, Tenant, PermissionEffect.Deny, RuleSource.UserGroup),
    };

    var decision = PermissionRules.Evaluate(rules, PermissionNames.DeviceRead, Resource.Device(Device, Tenant));

    Assert.False(decision.Allowed);
    Assert.Contains("user group", decision.Reason);
  }

  [Fact]
  public void DenyOnAnotherDevice_LeavesThisOneAllowed()
  {
    var rules = new[]
    {
      Rule(PermissionNames.DeviceRead, PermissionScopeKind.Tenant, Tenant),
      Rule(PermissionNames.DeviceRead, PermissionScopeKind.Device, Guid.NewGuid(), PermissionEffect.Deny),
    };

    Assert.True(PermissionRules.Evaluate(rules, PermissionNames.DeviceRead, Resource.Device(Device, Tenant)).Allowed);
  }

  [Theory]
  [InlineData(PermissionScopeKind.Server, true, true, true)]
  [InlineData(PermissionScopeKind.Tenant, true, true, false)]
  [InlineData(PermissionScopeKind.Device, false, true, false)]
  public void Scope_ReachesWhatItCovers(PermissionScopeKind scope, bool tenant, bool device, bool otherTenantsDevice)
  {
    var scopeId = scope switch
    {
      PermissionScopeKind.Tenant => Tenant,
      PermissionScopeKind.Device => Device,
      _ => (Guid?)null,
    };
    var rules = new[] { Rule(PermissionNames.DeviceRead, scope, scopeId) };

    Assert.Equal(tenant, PermissionRules.Evaluate(rules, PermissionNames.DeviceRead, Resource.Tenant(Tenant)).Allowed);
    Assert.Equal(device, PermissionRules.Evaluate(rules, PermissionNames.DeviceRead, Resource.Device(Device, Tenant)).Allowed);
    Assert.Equal(otherTenantsDevice, PermissionRules.Evaluate(rules, PermissionNames.DeviceRead, Resource.Device(Guid.NewGuid(), OtherTenant)).Allowed);
  }

  [Fact]
  public void AnotherPermission_DoesNotCount()
  {
    var rules = new[] { Rule(PermissionNames.TenantUsersRead, PermissionScopeKind.Tenant, Tenant) };

    Assert.False(PermissionRules.Evaluate(rules, PermissionNames.TenantUsersWrite, Resource.Tenant(Tenant)).Allowed);
  }

  [Fact]
  public void AllowAtAScopeThePermissionCannotHave_GrantsNothing()
  {
    // tenant.users.read only exists at tenant scope; a device-scope allow of it means nothing.
    var rules = new[] { new PermissionRule(PermissionNames.TenantUsersRead, PermissionEffect.Allow, PermissionScopeKind.Server, null, null, RuleSource.Direct) };

    Assert.False(PermissionRules.Evaluate(rules, PermissionNames.ServerTenantsRead, Resource.Server).Allowed);
    Assert.False(PermissionRules.Evaluate(rules, PermissionNames.TenantUsersRead, Resource.Tenant(Tenant)).Allowed);
  }

  [Fact]
  public void DenyAtAScopeThePermissionCannotHave_StillDenies()
  {
    var rules = new[]
    {
      Rule(PermissionNames.TenantUsersRead, PermissionScopeKind.Tenant, Tenant),
      Rule(PermissionNames.TenantUsersRead, PermissionScopeKind.Server, null, PermissionEffect.Deny),
    };

    Assert.False(PermissionRules.Evaluate(rules, PermissionNames.TenantUsersRead, Resource.Tenant(Tenant)).Allowed);
  }

  [Fact]
  public void Applicable_DropsDisabledRowsAndOtherTenantsRows()
  {
    var rows = new[]
    {
      Assignment(PermissionNames.TenantRead, PermissionScopeKind.Tenant, Tenant),
      Assignment(PermissionNames.TenantUsersRead, PermissionScopeKind.Tenant, Tenant, enabled: false),
      Assignment(PermissionNames.TenantSettingsRead, PermissionScopeKind.Tenant, OtherTenant),
    };

    var rules = PermissionRules.Applicable(rows, Tenant, RuleSource.Direct).ToList();

    Assert.Equal([PermissionNames.TenantRead], rules.Select(x => x.Permission));
  }

  [Fact]
  public void Applicable_TenantPrincipalCannotHoldAServerWideAllowOfATenantPermission()
  {
    var rows = new[]
    {
      Assignment(PermissionNames.DeviceRead, PermissionScopeKind.Server, null),
      Assignment(PermissionNames.ServerTenantsRead, PermissionScopeKind.Server, null),
      Assignment(PermissionNames.TenantRead, PermissionScopeKind.Server, null, PermissionEffect.Deny),
    };

    var tenantPrincipal = PermissionRules.Applicable(rows, Tenant, RuleSource.Direct).Select(x => x.Permission).ToList();
    var serverPrincipal = PermissionRules.Applicable(rows, null, RuleSource.Direct).Select(x => x.Permission).ToList();

    // Server-only permissions and denies stay; a server-wide device.read would reach every tenant's devices.
    Assert.Equal([PermissionNames.ServerTenantsRead, PermissionNames.TenantRead], tenantPrincipal);
    Assert.Equal(3, serverPrincipal.Count);
  }

  private static PermissionAssignmentRecord Assignment(
    string permission,
    PermissionScopeKind scope,
    Guid? tenantId,
    PermissionEffect effect = PermissionEffect.Allow,
    bool enabled = true) => new()
  {
    Permission = permission,
    Effect = effect,
    ScopeKind = scope,
    ScopeId = tenantId,
    OwningTenantId = tenantId,
    IsEnabled = enabled,
  };
}
