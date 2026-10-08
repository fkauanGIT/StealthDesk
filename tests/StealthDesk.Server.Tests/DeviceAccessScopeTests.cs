using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.Devices;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Server.Tests;

/// <summary>The device list filter decides exactly what the evaluator decides, device by device.</summary>
public class DeviceAccessScopeTests
{
  private static readonly Guid Tenant = Guid.NewGuid();
  private static readonly Guid OtherTenant = Guid.NewGuid();

  private static readonly DeviceRecord[] Devices =
  [
    .. Enumerable.Range(0, 4).Select(_ => new DeviceRecord { Id = Guid.NewGuid(), TenantId = Tenant }),
    .. Enumerable.Range(0, 2).Select(_ => new DeviceRecord { Id = Guid.NewGuid(), TenantId = OtherTenant }),
  ];

  [Fact]
  public void NoRules_ReadsNothing()
  {
    var scope = DeviceAccessScope.From([], Tenant);

    Assert.True(scope.IsEmpty);
    Assert.Empty(Visible(scope));
  }

  [Fact]
  public void TenantAllow_WithADeviceDeny_LeavesTheRestOfTheTenant()
  {
    var denied = Devices[0];
    var scope = DeviceAccessScope.From(
      [
        Rule(PermissionEffect.Allow, PermissionScopeKind.Tenant, Tenant),
        Rule(PermissionEffect.Deny, PermissionScopeKind.Device, denied.Id),
      ],
      Tenant);

    Assert.Equal(Devices.Where(x => x.TenantId == Tenant && x != denied), Visible(scope));
  }

  [Fact]
  public void ServerDeny_ReadsNothing_WhateverIsAllowed()
  {
    var scope = DeviceAccessScope.From(
      [
        Rule(PermissionEffect.Allow, PermissionScopeKind.Server, null),
        Rule(PermissionEffect.Deny, PermissionScopeKind.Server, null, RuleSource.UserGroup),
      ],
      null);

    Assert.Empty(Visible(scope));
  }

  [Fact]
  public void DeviceAllowOutsideTheTenantBoundary_ReadsNothing()
  {
    var scope = DeviceAccessScope.From([Rule(PermissionEffect.Allow, PermissionScopeKind.Device, Devices[^1].Id)], Tenant);

    Assert.Empty(Visible(scope));
  }

  [Fact]
  public void OtherPermissions_DontCount()
  {
    var scope = DeviceAccessScope.From([Rule(PermissionEffect.Allow, PermissionScopeKind.Tenant, Tenant) with { Permission = PermissionNames.TenantRead }], Tenant);

    Assert.True(scope.IsEmpty);
  }

  // Random rule sets over a few tenants and devices, including scopes device.read can't be granted at. The seed is
  // printed on failure so a counterexample can be replayed.
  [Theory]
  [InlineData(1)]
  [InlineData(2)]
  [InlineData(3)]
  public void AgreesWithTheEvaluator_OnRandomRules(int seed)
  {
    var random = new Random(seed);
    for (var round = 0; round < 500; round++)
    {
      var boundary = random.Next(3) == 0 ? (Guid?)null : Tenant;
      var rules = Enumerable.Range(0, random.Next(0, 6)).Select(_ => RandomRule(random)).ToList();
      var scope = DeviceAccessScope.From(rules, boundary);
      var visible = Visible(scope).ToHashSet();

      foreach (var device in Devices)
      {
        var expected = PermissionRules.Evaluate(rules, PermissionNames.DeviceRead, Resource.Device(device)).Allowed
          && (boundary is null || device.TenantId == boundary);
        Assert.True(
          expected == visible.Contains(device),
          $"Seed {seed}, round {round}: device {Array.IndexOf(Devices, device)} expected {(expected ? "visible" : "hidden")} with " +
          $"boundary {(boundary is null ? "none" : "tenant")} and rules [{string.Join("; ", rules.Select(Describe))}].");
      }
    }
  }

  private static List<DeviceRecord> Visible(DeviceAccessScope scope) => [.. scope.Apply(Devices.AsQueryable())];

  private static PermissionRule Rule(PermissionEffect effect, PermissionScopeKind scope, Guid? scopeId, RuleSource source = RuleSource.Direct) =>
    new(PermissionNames.DeviceRead, effect, scope, scopeId, scope == PermissionScopeKind.Server ? null : Tenant, source);

  private static PermissionRule RandomRule(Random random)
  {
    var scope = (PermissionScopeKind)random.Next(0, 5);
    Guid? scopeId = scope switch
    {
      PermissionScopeKind.Tenant => random.Next(2) == 0 ? Tenant : OtherTenant,
      PermissionScopeKind.Device or PermissionScopeKind.UserGroup => Devices[random.Next(Devices.Length)].Id,
      PermissionScopeKind.Server => null,
      _ => Guid.NewGuid(),
    };
    var permission = random.Next(6) == 0 ? PermissionNames.TenantRead : PermissionNames.DeviceRead;
    var effect = random.Next(3) == 0 ? PermissionEffect.Deny : PermissionEffect.Allow;
    var source = random.Next(2) == 0 ? RuleSource.Direct : RuleSource.UserGroup;
    return new PermissionRule(permission, effect, scope, scopeId, Tenant, source);
  }

  private static string Describe(PermissionRule rule)
  {
    var target = rule.ScopeId is not { } id ? ""
      : id == Tenant ? " tenant"
      : id == OtherTenant ? " other tenant"
      : Array.FindIndex(Devices, x => x.Id == id) is var index and >= 0 ? $" device {index}"
      : " unknown";
    return $"{rule.Permission} {rule.Effect} {rule.ScopeKind}{target} ({rule.Source})";
  }
}
