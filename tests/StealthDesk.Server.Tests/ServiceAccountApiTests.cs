using System.Net;
using Microsoft.Extensions.Time.Testing;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.ServiceAccounts;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Bootstrap;
using StealthDesk.Web.Server.ServiceAccounts;

namespace StealthDesk.Server.Tests;

/// <summary>
/// Service accounts: managed per tenant or for the server, each change logged; credentials shown once, revoked,
/// purged and cleaned up; the key signing in as the account, within its grants and its tenant; unrestricted server
/// accounts; and the bootstrap account from configuration.
/// </summary>
public class ServiceAccountApiTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task AnAccountsLife_LeavesOneEntryPerChange()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var account = await CreateAsync(admin, tenant, new CreateServiceAccountRequest(" GLPI sync ", " Pulls the inventory "));
    var updated = await admin.PutAsJsonAsync(One(account.Id, tenant), new UpdateServiceAccountRequest("GLPI", null, IsEnabled: true), Cancel);
    var credential = await AddCredentialAsync(admin, tenant, account.Id, "Primary");
    var revoked = await admin.DeleteAsync(Credential(account.Id, credential.Credential.Id, tenant), Cancel);
    var revokedAgain = await admin.DeleteAsync(Credential(account.Id, credential.Credential.Id, tenant), Cancel);
    var purged = await admin.DeleteAsync(Purge(account.Id, credential.Credential.Id, tenant), Cancel);
    var deleted = await admin.DeleteAsync(One(account.Id, tenant), Cancel);

    Assert.Equal("GLPI sync", account.Name);
    Assert.Equal("Pulls the inventory", account.Description);
    Assert.Equal(ServiceAccountKind.Tenant, account.Kind);
    Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, revokedAgain.StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, purged.StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    var log = await server.WithDbAsync(db => db.AuthorizationChanges.Where(x => x.OwningTenantId == tenant && x.Action.StartsWith("service-account")).OrderBy(x => x.CreatedAt).ToListAsync());
    Assert.Equal(
      [
        AuthorizationChangeActions.ServiceAccountCreated,
        AuthorizationChangeActions.ServiceAccountUpdated,
        AuthorizationChangeActions.ServiceAccountCredentialCreated,
        AuthorizationChangeActions.ServiceAccountCredentialRevoked,
        AuthorizationChangeActions.ServiceAccountCredentialDeleted,
        AuthorizationChangeActions.ServiceAccountDeleted,
      ],
      log.Select(x => x.Action));
    Assert.Contains("\"name\":\"GLPI sync\"", log[1].BeforeJson);
    Assert.Contains("\"name\":\"GLPI\"", log[1].AfterJson);
    Assert.All(log, x => Assert.DoesNotContain(credential.ApiKey.Split(':')[1], (x.BeforeJson ?? string.Empty) + x.AfterJson));
    Assert.False(await server.WithDbAsync(db => db.ServiceAccounts.AnyAsync()));
  }

  [Fact]
  public async Task AKey_SignsInAsTheAccount_ReachingOnlyItsGrants()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var account = await CreateAsync(admin, tenant, new CreateServiceAccountRequest("Reporter"));
    await GrantAsync(admin, tenant, account.Id, PermissionNames.TenantUsersRead);
    var key = await AddCredentialAsync(admin, tenant, account.Id, "Primary");
    using var script = WithKey(server, key.ApiKey);

    var users = await script.GetAsync($"{Routes.Users}?tenantId={tenant}", Cancel);
    var withoutTenant = await script.GetAsync(Routes.Users, Cancel);
    var assignments = await script.GetAsync($"{Routes.PermissionAssignments}?tenantId={tenant}", Cancel);
    var tokens = await script.GetAsync(Routes.PersonalAccessTokens, Cancel);

    Assert.Equal(HttpStatusCode.OK, users.StatusCode);
    Assert.Equal(HttpStatusCode.OK, withoutTenant.StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, assignments.StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, tokens.StatusCode);
    var stored = await server.WithDbAsync(db => db.ServiceAccountCredentials.SingleAsync());
    Assert.NotNull(stored.LastUsed);
    Assert.DoesNotContain(key.ApiKey.Split(':')[1], stored.HashedSecret);
  }

  [Fact]
  public async Task ATenantAccount_NeverSeesAnotherTenant()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("Accounts:EnablePublicRegistration", "true");
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var (other, otherTenant) = await RegisterAsync(server, "other@example.com");
    var mine = await DeviceInAsync(server, tenant);
    await DeviceInAsync(server, otherTenant);
    var account = await CreateAsync(admin, tenant, new CreateServiceAccountRequest("Inventory"));
    await GrantAsync(admin, tenant, account.Id, PermissionNames.TenantUsersRead);
    await GrantAsync(admin, tenant, account.Id, PermissionNames.DeviceRead);
    var key = await AddCredentialAsync(admin, tenant, account.Id, "Primary");
    using var script = WithKey(server, key.ApiKey);

    var devices = await script.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, Cancel);
    var otherUsers = await script.GetAsync($"{Routes.Users}?tenantId={otherTenant}", Cancel);
    var seenByOther = await other.GetAsync(One(account.Id, otherTenant), Cancel);
    var listedByOther = await other.GetFromJsonAsync<ServiceAccountList>(All(otherTenant), Cancel);
    var grantedByOther = await other.PostAsJsonAsync($"{Routes.PermissionAssignments}?tenantId={otherTenant}", Grant(account.Id, PermissionNames.TenantUsersRead), Cancel);

    Assert.Equal([mine], devices!.Select(x => x.Id));
    Assert.Equal(HttpStatusCode.Forbidden, otherUsers.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, seenByOther.StatusCode);
    Assert.Empty(listedByOther!.Items);
    Assert.Equal(HttpStatusCode.BadRequest, grantedByOther.StatusCode);
  }

  [Fact]
  public async Task ADisabledAccount_OrARevokedOrExpiredCredential_IsRefusedAtOnce_EvenRightAfterAUse()
  {
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    using var server = ServerHost.InMemory().WithServices(services => services.AddSingleton<TimeProvider>(clock));
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var account = await CreateAsync(admin, tenant, new CreateServiceAccountRequest("Script"));
    await GrantAsync(admin, tenant, account.Id, PermissionNames.TenantUsersRead);
    var revoked = await AddCredentialAsync(admin, tenant, account.Id, "To revoke");
    var expiring = await AddCredentialAsync(admin, tenant, account.Id, "Expiring", clock.GetUtcNow().AddDays(30));
    var kept = await AddCredentialAsync(admin, tenant, account.Id, "Kept");
    var users = $"{Routes.Users}?tenantId={tenant}";
    async Task<HttpStatusCode> Call(CreatedServiceAccountCredential credential)
    {
      using var script = WithKey(server, credential.ApiKey);
      return (await script.GetAsync(users, Cancel)).StatusCode;
    }

    Assert.Equal(HttpStatusCode.OK, await Call(revoked));
    Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(Credential(account.Id, revoked.Credential.Id, tenant), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, await Call(revoked));

    Assert.Equal(HttpStatusCode.OK, await Call(kept));
    Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(One(account.Id, tenant), new UpdateServiceAccountRequest("Script", null, IsEnabled: false), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, await Call(kept));
    var addToDisabled = await admin.PostAsJsonAsync(Credentials(account.Id, tenant), new CreateServiceAccountCredentialRequest("New", null), Cancel);
    Assert.Equal(HttpStatusCode.Forbidden, addToDisabled.StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(One(account.Id, tenant), new UpdateServiceAccountRequest("Script", null, IsEnabled: true), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.OK, await Call(kept));

    // Last: moving the clock also ends the administrator's session.
    Assert.Equal(HttpStatusCode.OK, await Call(expiring));
    clock.Advance(TimeSpan.FromDays(30));
    Assert.Equal(HttpStatusCode.Unauthorized, await Call(expiring));
  }

  // A valid key is remembered for a moment; the id alone, without the secret, must never pass.
  [Fact]
  public async Task TheCredentialIdAlone_NeverSignsIn_EvenRightAfterAValidUse()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var account = await CreateAsync(admin, tenant, new CreateServiceAccountRequest("Script"));
    await GrantAsync(admin, tenant, account.Id, PermissionNames.TenantUsersRead);
    var key = await AddCredentialAsync(admin, tenant, account.Id, "Primary");
    using var right = WithKey(server, key.ApiKey);
    using var wrong = WithKey(server, $"{key.Credential.Id}:{new string('x', 64)}");

    var first = await right.GetAsync($"{Routes.Users}?tenantId={tenant}", Cancel);
    var guessed = await wrong.GetAsync($"{Routes.Users}?tenantId={tenant}", Cancel);

    Assert.Equal(HttpStatusCode.OK, first.StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, guessed.StatusCode);
  }

  [Fact]
  public async Task RepeatedFailures_AreRefusedForAWhile_EvenWithTheRightKey()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var account = await CreateAsync(admin, tenant, new CreateServiceAccountRequest("Script"));
    await GrantAsync(admin, tenant, account.Id, PermissionNames.TenantUsersRead);
    var key = await AddCredentialAsync(admin, tenant, account.Id, "Primary");
    using var right = WithKey(server, key.ApiKey);
    using var wrong = WithKey(server, $"{key.Credential.Id}:{new string('x', 64)}");
    var users = $"{Routes.Users}?tenantId={tenant}";

    for (var i = 0; i < new ServiceAccountOptions().AuthFailureLimit; i++)
    {
      Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync(users, Cancel)).StatusCode);
    }

    Assert.Equal(HttpStatusCode.Unauthorized, (await right.GetAsync(users, Cancel)).StatusCode);
  }

  [Fact]
  public async Task ARestrictedServerAccount_ActsInAnyTenant_WithWhatTheServersAdministratorGranted()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var (otherAdmin, otherTenant) = await RegisterAsync(server, "other@example.com");
    var account = await CreateServerAsync(admin, new CreateServiceAccountRequest("Fleet report"));

    var byTenantAdmin = await otherAdmin.PostAsJsonAsync($"{Routes.PermissionAssignments}?tenantId={otherTenant}", Grant(account.Id, PermissionNames.TenantUsersRead), Cancel);
    var serverWide = await admin.PostAsJsonAsync($"{Routes.PermissionAssignments}?tenantId={tenant}", Grant(account.Id, PermissionNames.DeviceRead, PermissionScopeKind.Server), Cancel);
    var inTenant = await admin.PostAsJsonAsync($"{Routes.PermissionAssignments}?tenantId={tenant}", Grant(account.Id, PermissionNames.TenantUsersRead), Cancel);
    var serverWideToAUser = await admin.PostAsJsonAsync(
      $"{Routes.PermissionAssignments}?tenantId={tenant}",
      new CreatePermissionAssignmentRequest(PermissionPrincipalKind.User, (await MeAsync(admin)).Id, PermissionNames.DeviceRead, PermissionEffect.Allow, PermissionScopeKind.Server),
      Cancel);
    var mine = await DeviceInAsync(server, tenant);
    var theirs = await DeviceInAsync(server, otherTenant);
    var key = await AddCredentialAsync(admin, null, account.Id, "Primary");
    using var script = WithKey(server, key.ApiKey);
    var check = await admin.GetFromJsonAsync<EffectivePermission>(
      $"{Routes.EffectivePermission(account.Id)}?tenantId={tenant}&principalKind=ServiceAccount&permission={PermissionNames.TenantUsersRead}&scopeKind=Tenant", Cancel);

    Assert.Equal(HttpStatusCode.Forbidden, byTenantAdmin.StatusCode);
    Assert.Equal(HttpStatusCode.Created, serverWide.StatusCode);
    Assert.Equal(HttpStatusCode.Created, inTenant.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, serverWideToAUser.StatusCode);
    Assert.Equal(new[] { mine, theirs }.Order(), (await script.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, Cancel))!.Select(x => x.Id).Order());
    Assert.Equal(HttpStatusCode.OK, (await script.GetAsync($"{Routes.Users}?tenantId={tenant}", Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await script.GetAsync($"{Routes.Users}?tenantId={otherTenant}", Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await script.GetAsync($"{Routes.Users}", Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await script.PostAsJsonAsync($"{Routes.Invites}?tenantId={tenant}", new { inviteeEmail = "new@example.com" }, Cancel)).StatusCode);
    Assert.True(check!.IsAllowed);
  }

  [Fact]
  public async Task AnUnrestrictedServerAccount_DoesAnything_AndOnlyTheServersPermissionManagerCreatesOne()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var (_, otherTenant) = await RegisterAsync(server, "other@example.com");
    var device = await DeviceInAsync(server, otherTenant);
    var manager = await TestAccounts.CreateUserAsync(server, "manager@example.com", tenant);
    await TestPermissions.AssignAsync(server, manager, PermissionNames.ServerServiceAccountsWrite, PermissionScopeKind.Server);
    using var managerClient = await ClientForAsync(server, "manager@example.com");

    var byManager = await managerClient.PostAsJsonAsync(Routes.ServerServiceAccounts, new CreateServiceAccountRequest("Root", AccessMode: ServiceAccountAccessMode.Unrestricted), Cancel);
    var restrictedByManager = await managerClient.PostAsJsonAsync(Routes.ServerServiceAccounts, new CreateServiceAccountRequest("Narrow"), Cancel);
    var unrestrictedInTenant = await admin.PostAsJsonAsync(All(tenant), new CreateServiceAccountRequest("Root", AccessMode: ServiceAccountAccessMode.Unrestricted), Cancel);
    var root = await CreateServerAsync(admin, new CreateServiceAccountRequest("Root", AccessMode: ServiceAccountAccessMode.Unrestricted));
    var key = await AddCredentialAsync(admin, null, root.Id, "Primary");
    using var script = WithKey(server, key.ApiKey);

    Assert.Equal(HttpStatusCode.Forbidden, byManager.StatusCode);
    Assert.Equal(HttpStatusCode.Created, restrictedByManager.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, unrestrictedInTenant.StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await script.GetAsync($"{Routes.PermissionAssignments}?tenantId={otherTenant}&principalKind=User&principalId={Guid.NewGuid()}", Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await script.GetAsync($"{Routes.Invites}?tenantId={otherTenant}", Cancel)).StatusCode);
    Assert.Contains(device, (await script.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, Cancel))!.Select(x => x.Id));
  }

  [Fact]
  public async Task Names_AreUniquePerTenant_AndAmongServerAccounts()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("Accounts:EnablePublicRegistration", "true");
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var (other, otherTenant) = await RegisterAsync(server, "other@example.com");
    await CreateAsync(admin, tenant, new CreateServiceAccountRequest("Backup"));
    var second = await CreateAsync(admin, tenant, new CreateServiceAccountRequest("Second"));

    var again = await admin.PostAsJsonAsync(All(tenant), new CreateServiceAccountRequest("Backup"), Cancel);
    var renamedOnto = await admin.PutAsJsonAsync(One(second.Id, tenant), new UpdateServiceAccountRequest("Backup", null, true), Cancel);
    var otherTenantSame = await other.PostAsJsonAsync(All(otherTenant), new CreateServiceAccountRequest("Backup"), Cancel);
    var serverSame = await admin.PostAsJsonAsync(Routes.ServerServiceAccounts, new CreateServiceAccountRequest("Backup"), Cancel);
    var serverAgain = await admin.PostAsJsonAsync(Routes.ServerServiceAccounts, new CreateServiceAccountRequest("Backup"), Cancel);
    var simultaneous = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => admin.PostAsJsonAsync(All(tenant), new CreateServiceAccountRequest("Race"), Cancel)));

    Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    Assert.Equal(HttpStatusCode.Conflict, renamedOnto.StatusCode);
    Assert.Equal(HttpStatusCode.Created, otherTenantSame.StatusCode);
    Assert.Equal(HttpStatusCode.Created, serverSame.StatusCode);
    Assert.Equal(HttpStatusCode.Conflict, serverAgain.StatusCode);
    Assert.Single(simultaneous, x => x.StatusCode == HttpStatusCode.Created);
    Assert.All(simultaneous.Where(x => x.StatusCode != HttpStatusCode.Created), x => Assert.Equal(HttpStatusCode.Conflict, x.StatusCode));
  }

  [Fact]
  public async Task Requests_AreChecked()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var account = await CreateAsync(admin, tenant, new CreateServiceAccountRequest("Script"));
    var live = await AddCredentialAsync(admin, tenant, account.Id, "Live");

    async Task<HttpStatusCode> Create(CreateServiceAccountRequest request) => (await admin.PostAsJsonAsync(All(tenant), request, Cancel)).StatusCode;
    async Task<HttpStatusCode> Add(CreateServiceAccountCredentialRequest request) => (await admin.PostAsJsonAsync(Credentials(account.Id, tenant), request, Cancel)).StatusCode;

    Assert.Equal(HttpStatusCode.BadRequest, await Create(new CreateServiceAccountRequest(" ")));
    Assert.Equal(HttpStatusCode.BadRequest, await Create(new CreateServiceAccountRequest(new string('n', CreateServiceAccountRequest.NameMax + 1))));
    Assert.Equal(HttpStatusCode.BadRequest, await Create(new CreateServiceAccountRequest("Long", new string('d', CreateServiceAccountRequest.DescriptionMax + 1))));
    Assert.Equal(HttpStatusCode.BadRequest, await Add(new CreateServiceAccountCredentialRequest(" ", null)));
    Assert.Equal(HttpStatusCode.BadRequest, await Add(new CreateServiceAccountCredentialRequest("Past", DateTimeOffset.UtcNow.AddMinutes(-1))));
    Assert.Equal(HttpStatusCode.Created, await Add(new CreateServiceAccountCredentialRequest("Future", DateTimeOffset.UtcNow.AddDays(7))));
    Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync(Purge(account.Id, live.Credential.Id, tenant), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync(Credential(account.Id, Guid.NewGuid(), tenant), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync(Credential(Guid.NewGuid(), live.Credential.Id, tenant), Cancel)).StatusCode);
  }

  [Fact]
  public async Task ManagingNeedsItsPermissions_AndAnAccountCantDeleteItself_AndDeletingTakesItsPermissions()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var reader = await TestAccounts.CreateUserAsync(server, "reader@example.com", tenant);
    await TestPermissions.AssignAsync(server, reader, PermissionNames.ServiceAccountRead, PermissionScopeKind.Tenant, tenant);
    using var readerClient = await ClientForAsync(server, "reader@example.com");
    var account = await CreateAsync(admin, tenant, new CreateServiceAccountRequest("Automation"));
    await GrantAsync(admin, tenant, account.Id, PermissionNames.ServiceAccountWrite);
    var key = await AddCredentialAsync(admin, tenant, account.Id, "Primary");
    using var script = WithKey(server, key.ApiKey);

    var listed = await readerClient.GetAsync(All(tenant), Cancel);
    var createdByReader = await readerClient.PostAsJsonAsync(All(tenant), new CreateServiceAccountRequest("Mine"), Cancel);
    var keyByReader = await readerClient.PostAsJsonAsync(Credentials(account.Id, tenant), new CreateServiceAccountCredentialRequest("Mine", null), Cancel);
    var serverListByReader = await readerClient.GetAsync(Routes.ServerServiceAccounts, Cancel);
    var itself = await script.DeleteAsync(One(account.Id, tenant), Cancel);
    var deleted = await admin.DeleteAsync(One(account.Id, tenant), Cancel);

    Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, createdByReader.StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, keyByReader.StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, serverListByReader.StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, itself.StatusCode);
    Assert.Contains("can't delete itself", await itself.Content.ReadAsStringAsync(Cancel));
    Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.PermissionAssignments.AnyAsync(x => x.PrincipalId == account.Id)));
    Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync(All(tenant), Cancel)).StatusCode);
  }

  [Fact]
  public async Task TheCleanup_DeletesCredentialsDeadLongerThanItsDays_AndLogsEach()
  {
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    using var server = ServerHost.InMemory().WithServices(services => services.AddSingleton<TimeProvider>(clock));
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var account = await CreateAsync(admin, tenant, new CreateServiceAccountRequest("Script"));
    var oldRevoked = await AddCredentialAsync(admin, tenant, account.Id, "Old revoked");
    var oldExpired = await AddCredentialAsync(admin, tenant, account.Id, "Old expired", clock.GetUtcNow().AddDays(1));
    var live = await AddCredentialAsync(admin, tenant, account.Id, "Live");
    await admin.DeleteAsync(Credential(account.Id, oldRevoked.Credential.Id, tenant), Cancel);
    clock.Advance(TimeSpan.FromDays(31));
    admin = await ClientForAsync(server, "admin@example.com");
    var recentRevoked = await AddCredentialAsync(admin, tenant, account.Id, "Recently revoked");
    await admin.DeleteAsync(Credential(account.Id, recentRevoked.Credential.Id, tenant), Cancel);
    clock.Advance(TimeSpan.FromDays(1));

    // Moving the clock also wakes the cleanup that runs on its own; either way, the outcome is the same.
    await server.Services.GetRequiredService<ServiceAccountCredentialCleanup>().CleanAsync(Cancel);

    var left = await server.WithDbAsync(db => db.ServiceAccountCredentials.Select(x => x.Id).ToListAsync());
    Assert.Equal(new[] { live.Credential.Id, recentRevoked.Credential.Id }.Order(), left.Order());
    var entries = await server.WithDbAsync(db => db.AuthorizationChanges.Where(x => x.Action == AuthorizationChangeActions.ServiceAccountCredentialDeleted).ToListAsync());
    Assert.Equal(new[] { oldRevoked.Credential.Id, oldExpired.Credential.Id }.Order(), entries.Select(x => x.TargetId!.Value).Order());
    Assert.All(entries, x => Assert.Equal(AuthorizationChangeActors.System, x.ActorKind));
    Assert.All(entries, x => Assert.Equal(tenant, x.OwningTenantId));
  }

  [Fact]
  public async Task TheBootstrapAccount_IsCreatedOnce_Unrestricted_WithTheConfiguredKey()
  {
    var credentialId = Guid.NewGuid();
    var accountId = Guid.NewGuid();
    var secret = new string('s', BootstrapOptions.MinimumSecretLength);
    using var server = ServerHost.InMemory()
      .With("Bootstrap:ServerServiceAccountName", "Provisioning")
      .With("Bootstrap:ServerServiceAccountId", accountId.ToString())
      .With("Bootstrap:ServerServiceAccountCredentialId", credentialId.ToString())
      .With("Bootstrap:ServerServiceAccountCredentialSecret", secret);
    var (_, tenant) = await RegisterAsync(server, "admin@example.com");
    using var script = WithKey(server, $"{credentialId}:{secret}");

    var users = await script.GetAsync($"{Routes.Users}?tenantId={tenant}", Cancel);
    using var restarted = server.Restarted();
    using var afterRestart = WithKey(restarted, $"{credentialId}:{secret}");
    var usersAfterRestart = await afterRestart.GetAsync($"{Routes.Users}?tenantId={tenant}", Cancel);

    Assert.Equal(HttpStatusCode.OK, users.StatusCode);
    Assert.Equal(HttpStatusCode.OK, usersAfterRestart.StatusCode);
    var account = await restarted.WithDbAsync(db => db.ServiceAccounts.SingleAsync());
    Assert.Equal(accountId, account.Id);
    Assert.Equal(ServiceAccountKind.Server, account.Kind);
    Assert.Equal(ServiceAccountAccessMode.Unrestricted, account.AccessMode);
    Assert.True(await server.WithDbAsync(db => db.AuthorizationChanges.AnyAsync(x => x.Action == AuthorizationChangeActions.ServiceAccountCreated && x.TargetId == accountId)));
  }

  [Theory]
  [InlineData("Provisioning", true, null)]
  [InlineData(null, true, "a-secret-long-enough-to-be-accepted-here")]
  [InlineData("Provisioning", false, "a-secret-long-enough-to-be-accepted-here")]
  [InlineData("Provisioning", true, "too-short")]
  public void AnIncompleteBootstrap_StopsTheServer(string? name, bool credentialId, string? secret)
  {
    using var server = ServerHost.InMemory()
      .With("Bootstrap:ServerServiceAccountName", name)
      .With("Bootstrap:ServerServiceAccountCredentialId", credentialId ? Guid.NewGuid().ToString() : null)
      .With("Bootstrap:ServerServiceAccountCredentialSecret", secret);

    var refusal = server.RefusalToStart();

    Assert.Contains("Bootstrap:ServerServiceAccount", refusal, StringComparison.Ordinal);
  }

  [Fact]
  public async Task TheDatabase_RefusesAServerAccountWithATenant_AndATenantAccountWithout()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var tenant = await TestTenants.CreateAsync(server);

    Task Insert(string kind, Guid? tenantId) => server.WithDbAsync(db => db.Database.ExecuteSqlAsync(
      $"""INSERT INTO service_accounts ("Id", "Kind", "TenantId", "Name", "IsEnabled", "AccessMode", "CreatedAt") VALUES ({Guid.NewGuid()}, {kind}, {tenantId}, {"x" + Guid.NewGuid()}, true, 'Restricted', now())"""));

    await Insert("Tenant", tenant);
    await Insert("Server", null);
    await Assert.ThrowsAnyAsync<Exception>(() => Insert("Server", tenant));
    await Assert.ThrowsAnyAsync<Exception>(() => Insert("Tenant", null));
    // In memory there is no constraint; the save is checked before it goes out.
    using var inMemory = ServerHost.InMemory();
    await Assert.ThrowsAsync<InvalidOperationException>(() => inMemory.WithDbAsync(db =>
    {
      db.ServiceAccounts.Add(new ServiceAccountRecord { Id = Guid.NewGuid(), Kind = ServiceAccountKind.Tenant, Name = "No tenant" });
      return db.SaveChangesAsync();
    }));
  }

  private static string All(Guid tenant) => $"{Routes.ServiceAccounts}?tenantId={tenant}";

  private static string Base(Guid? tenant) => tenant is null ? Routes.ServerServiceAccounts : Routes.ServiceAccounts;

  private static string Query(Guid? tenant) => tenant is null ? string.Empty : $"?tenantId={tenant}";

  private static string One(Guid id, Guid? tenant) => Routes.ServiceAccount(Base(tenant), id) + Query(tenant);

  private static string Credentials(Guid id, Guid? tenant) => Routes.ServiceAccountCredentials(Base(tenant), id) + Query(tenant);

  private static string Credential(Guid id, Guid credentialId, Guid? tenant) => Routes.ServiceAccountCredential(Base(tenant), id, credentialId) + Query(tenant);

  private static string Purge(Guid id, Guid credentialId, Guid? tenant) => Routes.ServiceAccountCredentialPurge(Base(tenant), id, credentialId) + Query(tenant);

  private static CreatePermissionAssignmentRequest Grant(Guid accountId, string permission, PermissionScopeKind scope = PermissionScopeKind.Tenant) =>
    new(PermissionPrincipalKind.ServiceAccount, accountId, permission, PermissionEffect.Allow, scope);

  private static async Task GrantAsync(HttpClient admin, Guid tenant, Guid accountId, string permission)
  {
    var response = await admin.PostAsJsonAsync($"{Routes.PermissionAssignments}?tenantId={tenant}", Grant(accountId, permission), Cancel);
    Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Cancel));
  }

  private static async Task<ServiceAccount> CreateAsync(HttpClient client, Guid tenant, CreateServiceAccountRequest request)
  {
    var response = await client.PostAsJsonAsync(All(tenant), request, Cancel);
    Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Cancel));
    return (await response.Content.ReadFromJsonAsync<ServiceAccount>(Cancel))!;
  }

  private static async Task<ServiceAccount> CreateServerAsync(HttpClient client, CreateServiceAccountRequest request)
  {
    var response = await client.PostAsJsonAsync(Routes.ServerServiceAccounts, request, Cancel);
    Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Cancel));
    return (await response.Content.ReadFromJsonAsync<ServiceAccount>(Cancel))!;
  }

  private static async Task<CreatedServiceAccountCredential> AddCredentialAsync(HttpClient client, Guid? tenant, Guid accountId, string name, DateTimeOffset? expiresAt = null)
  {
    var response = await client.PostAsJsonAsync(Credentials(accountId, tenant), new CreateServiceAccountCredentialRequest(name, expiresAt), Cancel);
    Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Cancel));
    return (await response.Content.ReadFromJsonAsync<CreatedServiceAccountCredential>(Cancel))!;
  }

  private static HttpClient WithKey(ServerHost server, string key)
  {
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add(ServiceAccountHeader.Name, key);
    return client;
  }

  private static Task<Guid> DeviceInAsync(ServerHost server, Guid tenant) => server.WithDbAsync(async db =>
  {
    var device = new DeviceRecord { Id = Guid.NewGuid(), TenantId = tenant, Name = "PC" };
    db.Devices.Add(device);
    await db.SaveChangesAsync();
    return device.Id;
  });

  private static async Task<CurrentUser> MeAsync(HttpClient client) =>
    (await client.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel))!;

  private static async Task<(HttpClient Client, Guid TenantId)> RegisterAsync(ServerHost server, string email)
  {
    using var anonymous = TestAccounts.Client(server);
    var response = await anonymous.PostAsJsonAsync($"{Routes.Auth}/register", new { email, password = TestAccounts.Password }, Cancel);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var client = await ClientForAsync(server, email);
    return (client, (await MeAsync(client)).TenantId);
  }

  private static async Task<HttpClient> ClientForAsync(ServerHost server, string email)
  {
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
    return client;
  }
}
