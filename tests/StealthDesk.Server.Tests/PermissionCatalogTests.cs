using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Server.Tests;

/// <summary>The catalog, the presets and the policies agree with each other.</summary>
public class PermissionCatalogTests
{
  private static IEnumerable<string> DeclaredNames() =>
    typeof(PermissionNames).GetFields(BindingFlags.Public | BindingFlags.Static)
      .Where(x => x.IsLiteral)
      .Select(x => (string)x.GetRawConstantValue()!);

  [Fact]
  public void EveryDeclaredPermission_IsDescribedInTheCatalog()
  {
    var missing = DeclaredNames().Where(x => !PermissionCatalog.Contains(x)).ToList();

    Assert.Empty(missing);
    Assert.Equal(DeclaredNames().Count(), PermissionCatalog.All.Count);
  }

  [Fact]
  public void EveryPermission_HasACategoryNameDescriptionAndScope()
  {
    Assert.All(PermissionCatalog.All, x =>
    {
      Assert.False(string.IsNullOrWhiteSpace(x.Category));
      Assert.False(string.IsNullOrWhiteSpace(x.DisplayName));
      Assert.False(string.IsNullOrWhiteSpace(x.Description));
      Assert.NotEmpty(x.Scopes);
      Assert.DoesNotContain(PermissionScopeKind.Unknown, x.Scopes);
    });
  }

  [Fact]
  public void EveryPresetPermission_IsInTheCatalog()
  {
    var unknown = PermissionPresets.All.SelectMany(x => x.Value).Where(x => !PermissionCatalog.Contains(x)).ToList();

    Assert.Empty(unknown);
  }

  [Theory]
  [InlineData(PermissionNames.ServerTenantsRead, PermissionScopeKind.Server)]
  [InlineData(PermissionNames.TenantUsersRead, PermissionScopeKind.Tenant)]
  [InlineData(PermissionNames.DeviceRead, PermissionScopeKind.Tenant)]
  [InlineData(PermissionNames.UserGroupAssignUsers, PermissionScopeKind.Tenant)]
  public void Presets_GrantInsideTheTenantUnlessThePermissionOnlyExistsOnTheServer(string permission, PermissionScopeKind expected) =>
    Assert.Equal(expected, PermissionCatalog.Find(permission)!.PresetScope);

  [Fact]
  public void AccessManagement_CannotBeRemovedFromYourself()
  {
    var locked = PermissionCatalog.All.Where(x => !x.SelfRemovable).Select(x => x.Name).Order().ToList();

    Assert.Equal([PermissionNames.ServerPermissionsWrite, PermissionNames.TenantPermissionsDeny, PermissionNames.TenantPermissionsWrite], locked);
  }

  [Fact]
  public async Task EveryPermission_IsAPolicyTheServerKnows()
  {
    using var server = ServerHost.InMemory();
    var policies = server.Services.GetRequiredService<IAuthorizationPolicyProvider>();

    foreach (var permission in PermissionCatalog.All)
    {
      var policy = await policies.GetPolicyAsync(PermissionPolicies.For(permission.Name));
      Assert.NotNull(policy);
      Assert.Equal(permission.Name, Assert.Single(policy.Requirements.OfType<PermissionRequirement>()).Permission);
    }
  }
}
