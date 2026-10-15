using System.Net;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.InstallerKeys;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.ServiceAccounts;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.InstallerKeys;

namespace StealthDesk.Server.Tests;

/// <summary>
/// Installer keys over the API: created with the rules of their type and shown once, renamed and revoked with one log
/// entry each, seen by their creator or by whoever manages them all, and their installs kept for the history set.
/// </summary>
public class InstallerKeyApiTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task AKeysLife_LeavesOneEntryPerChange_AndNeverItsSecret()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var created = await CreateAsync(admin, tenant, new CreateInstallerKeyRequest(InstallerKeyType.Persistent, " Lab PCs "));
    var listed = await ListAsync(admin, tenant);
    var renamed = await admin.PutAsJsonAsync(One(created.Key.Id, tenant), new RenameInstallerKeyRequest("Lab"), Cancel);
    var usages = await admin.GetFromJsonAsync<InstallerKeyUsageList>(Usages(created.Key.Id, tenant), Cancel);
    var deleted = await admin.DeleteAsync(One(created.Key.Id, tenant), Cancel);

    var secret = created.FullKey.Split(':')[1];
    Assert.Equal($"{created.Key.Id}", created.FullKey.Split(':')[0]);
    Assert.Equal(64, secret.Length);
    Assert.Equal("Lab PCs", created.Key.Name);
    var key = Assert.Single(listed);
    Assert.Equal(InstallerKeyCreatorKind.User, key.CreatorKind);
    Assert.Equal("admin@example.com", key.CreatorName);
    Assert.Equal(0, key.UsageCount);
    Assert.Null(key.ExpiresAt);
    Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
    Assert.Empty(usages!.Items);
    Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.InstallerKeys.AnyAsync()));

    var log = await server.WithDbAsync(db => db.AuthorizationChanges.Where(x => x.Action.StartsWith("installer-key")).OrderBy(x => x.CreatedAt).ToListAsync());
    Assert.Equal(
      [AuthorizationChangeActions.InstallerKeyCreated, AuthorizationChangeActions.InstallerKeyRenamed, AuthorizationChangeActions.InstallerKeyDeleted],
      log.Select(x => x.Action));
    Assert.All(log, x => Assert.Equal(tenant, x.OwningTenantId));
    Assert.All(log, x => Assert.Equal(AuthorizationChangeActors.User, x.ActorKind));
    Assert.Contains("\"name\":\"Lab PCs\"", log[1].BeforeJson);
    Assert.Contains("\"name\":\"Lab\"", log[1].AfterJson);
    Assert.All(log, x => Assert.DoesNotContain(secret, (x.BeforeJson ?? string.Empty) + x.AfterJson));
  }

  [Fact]
  public async Task EachType_KeepsItsOwnRules()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var inAWeek = DateTimeOffset.UtcNow.AddDays(7);

    var usage = await CreateAsync(admin, tenant, new CreateInstallerKeyRequest(InstallerKeyType.UsageBased, AllowedUses: 3, ExpiresAt: inAWeek));
    var time = await CreateAsync(admin, tenant, new CreateInstallerKeyRequest(InstallerKeyType.TimeBased, ExpiresAt: inAWeek));
    var persistent = await CreateAsync(admin, tenant, new CreateInstallerKeyRequest(InstallerKeyType.Persistent, AllowedUses: 3, ExpiresAt: inAWeek));

    Assert.Equal(3, usage.Key.AllowedUses);
    Assert.InRange(usage.Key.ExpiresAt!.Value, DateTimeOffset.UtcNow.AddHours(23), DateTimeOffset.UtcNow.AddHours(25));
    Assert.Null(time.Key.AllowedUses);
    Assert.Equal(inAWeek, time.Key.ExpiresAt!.Value, TimeSpan.FromSeconds(1));
    Assert.Null(persistent.Key.AllowedUses);
    Assert.Null(persistent.Key.ExpiresAt);
  }

  [Theory]
  [InlineData(InstallerKeyType.UsageBased, null, null)]
  [InlineData(InstallerKeyType.UsageBased, 0, null)]
  [InlineData(InstallerKeyType.TimeBased, null, null)]
  [InlineData(InstallerKeyType.TimeBased, null, -5)]
  [InlineData((InstallerKeyType)9, null, null)]
  public async Task AKeyMissingWhatItsTypeNeeds_IsRefused(InstallerKeyType type, int? uses, int? expiresInMinutes)
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var response = await admin.PostAsJsonAsync(
      All(tenant),
      new CreateInstallerKeyRequest(type, AllowedUses: uses, ExpiresAt: expiresInMinutes is { } minutes ? DateTimeOffset.UtcNow.AddMinutes(minutes) : null),
      Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.InstallerKeys.AnyAsync()));
  }

  [Fact]
  public async Task ANameTooLong_IsRefused()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var key = await CreateAsync(admin, tenant, new CreateInstallerKeyRequest(InstallerKeyType.Persistent));
    var name = new string('x', CreateInstallerKeyRequest.NameMax + 1);

    var created = await admin.PostAsJsonAsync(All(tenant), new CreateInstallerKeyRequest(InstallerKeyType.Persistent, name), Cancel);
    var renamed = await admin.PutAsJsonAsync(One(key.Key.Id, tenant), new RenameInstallerKeyRequest(name), Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);
  }

  [Fact]
  public async Task WithoutManageAll_OnlyOwnKeysAreSeen_AndOthersAreNotFound()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var installer = await TestAccounts.CreateUserAsync(server, "installer@example.com", tenant);
    await TestPermissions.AssignAsync(server, installer, PermissionNames.InstallerKeyRead, PermissionScopeKind.Tenant, tenant);
    await TestPermissions.AssignAsync(server, installer, PermissionNames.InstallerKeyWrite, PermissionScopeKind.Tenant, tenant);
    using var installerClient = await ClientForAsync(server, "installer@example.com");

    var adminsKey = await CreateAsync(admin, tenant, new CreateInstallerKeyRequest(InstallerKeyType.Persistent, "Admin's"));
    var ownKey = await CreateAsync(installerClient, tenant, new CreateInstallerKeyRequest(InstallerKeyType.Persistent, "Mine"));

    var seenByInstaller = await ListAsync(installerClient, tenant);
    var renameOther = await installerClient.PutAsJsonAsync(One(adminsKey.Key.Id, tenant), new RenameInstallerKeyRequest("Taken"), Cancel);
    var usagesOther = await installerClient.GetAsync(Usages(adminsKey.Key.Id, tenant), Cancel);
    var deleteOther = await installerClient.DeleteAsync(One(adminsKey.Key.Id, tenant), Cancel);
    var seenByAdmin = await ListAsync(admin, tenant);
    var adminDeletesTheirs = await admin.DeleteAsync(One(ownKey.Key.Id, tenant), Cancel);

    Assert.Equal(["Mine"], seenByInstaller.Select(x => x.Name));
    Assert.Equal(HttpStatusCode.NotFound, renameOther.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, usagesOther.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, deleteOther.StatusCode);
    Assert.Equal(["Admin's", "Mine"], seenByAdmin.Select(x => x.Name).Order());
    Assert.Equal(HttpStatusCode.NoContent, adminDeletesTheirs.StatusCode);
    Assert.Equal("Admin's", (await server.WithDbAsync(db => db.InstallerKeys.SingleAsync())).Name);
  }

  [Fact]
  public async Task ReadingAndWriting_TakeTheirPermissions()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (_, tenant) = await RegisterAsync(server, "admin@example.com");
    var reader = await TestAccounts.CreateUserAsync(server, "reader@example.com", tenant);
    await TestPermissions.AssignAsync(server, reader, PermissionNames.InstallerKeyRead, PermissionScopeKind.Tenant, tenant);
    await TestAccounts.CreateUserAsync(server, "nobody@example.com", tenant);
    using var readerClient = await ClientForAsync(server, "reader@example.com");
    using var nobody = await ClientForAsync(server, "nobody@example.com");

    var readerLists = await readerClient.GetAsync(All(tenant), Cancel);
    var readerCreates = await readerClient.PostAsJsonAsync(All(tenant), new CreateInstallerKeyRequest(InstallerKeyType.Persistent), Cancel);
    var nobodyLists = await nobody.GetAsync(All(tenant), Cancel);

    Assert.Equal(HttpStatusCode.OK, readerLists.StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, readerCreates.StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, nobodyLists.StatusCode);
  }

  [Fact]
  public async Task ATenantsKeys_AreOutOfReachOfAnotherTenant()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("Accounts:EnablePublicRegistration", "true");
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var (other, _) = await RegisterAsync(server, "other@example.com");
    var key = await CreateAsync(admin, tenant, new CreateInstallerKeyRequest(InstallerKeyType.Persistent));

    var list = await other.GetAsync(All(tenant), Cancel);
    var delete = await other.DeleteAsync(Routes.InstallerKey(key.Key.Id), Cancel);

    Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    Assert.True(await server.WithDbAsync(db => db.InstallerKeys.AnyAsync()));
  }

  [Fact]
  public async Task AServerAccount_MakesKeysForTheTenantItNames()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var account = await admin.PostAsJsonAsync(Routes.ServerServiceAccounts, new CreateServiceAccountRequest("Deployer", AccessMode: ServiceAccountAccessMode.Unrestricted), Cancel);
    var accountId = (await account.Content.ReadFromJsonAsync<ServiceAccount>(Cancel))!.Id;
    var credential = await admin.PostAsJsonAsync(Routes.ServiceAccountCredentials(Routes.ServerServiceAccounts, accountId), new CreateServiceAccountCredentialRequest("Primary", null), Cancel);
    using var script = TestAccounts.Client(server);
    script.DefaultRequestHeaders.Add(ServiceAccountHeader.Name, (await credential.Content.ReadFromJsonAsync<CreatedServiceAccountCredential>(Cancel))!.ApiKey);

    var withoutTenant = await script.PostAsJsonAsync(Routes.InstallerKeys, new CreateInstallerKeyRequest(InstallerKeyType.Persistent), Cancel);
    var unknownTenant = await script.PostAsJsonAsync(All(Guid.NewGuid()), new CreateInstallerKeyRequest(InstallerKeyType.Persistent), Cancel);
    var created = await CreateAsync(script, tenant, new CreateInstallerKeyRequest(InstallerKeyType.Persistent));
    var seenByAdmin = Assert.Single(await ListAsync(admin, tenant));

    Assert.Equal(HttpStatusCode.Forbidden, withoutTenant.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, unknownTenant.StatusCode);
    Assert.Equal(InstallerKeyCreatorKind.ServerServiceAccount, created.Key.CreatorKind);
    Assert.Equal(accountId, seenByAdmin.CreatorId);
    Assert.Equal("Deployer", seenByAdmin.CreatorName);
    var entry = await server.WithDbAsync(db => db.AuthorizationChanges.SingleAsync(x => x.Action == AuthorizationChangeActions.InstallerKeyCreated));
    Assert.Equal(AuthorizationChangeActors.ServiceAccount, entry.ActorKind);
  }

  [Fact]
  public async Task SpentKeys_LeaveTheList()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var single = await CreateAsync(admin, tenant, new CreateInstallerKeyRequest(InstallerKeyType.UsageBased, "Once", AllowedUses: 1));
    var twice = await CreateAsync(admin, tenant, new CreateInstallerKeyRequest(InstallerKeyType.UsageBased, "Twice", AllowedUses: 2));
    await TestInstallerKeys.CreateAsync(server, tenant, InstallerKeyType.TimeBased, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
    await using var agent = await TestAgent.ConnectAsync(server, withInstallerKey: false);

    Assert.True((await agent.ReportAsync(TestAgent.Report(Guid.NewGuid()) with { InstallerKey = single.FullKey })).Accepted);
    Assert.True((await agent.ReportAsync(TestAgent.Report(Guid.NewGuid(), "PC-2") with { InstallerKey = twice.FullKey })).Accepted);

    var left = Assert.Single(await ListAsync(admin, tenant));
    Assert.Equal("Twice", left.Name);
    Assert.Equal(1, left.UsageCount);
    var usage = Assert.Single((await admin.GetFromJsonAsync<InstallerKeyUsageList>(Usages(twice.Key.Id, tenant), Cancel))!.Items);
    Assert.Equal((await server.WithDbAsync(db => db.Devices.SingleAsync(x => x.Name == "PC-2"))).Id, usage.DeviceId);
  }

  [Fact]
  public async Task UsagesOlderThanTheHistory_AreLeftOut_AndCleanedUp()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("InstallerKeys:UsageHistoryDays", "30");
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var key = await CreateAsync(admin, tenant, new CreateInstallerKeyRequest(InstallerKeyType.Persistent));
    var recent = Guid.NewGuid();
    await server.WithDbAsync(async db =>
    {
      db.InstallerKeyUsages.Add(new InstallerKeyUsageRecord { Id = Guid.NewGuid(), InstallerKeyId = key.Key.Id, TenantId = tenant, DeviceId = Guid.NewGuid(), UsedAt = DateTimeOffset.UtcNow.AddDays(-31) });
      db.InstallerKeyUsages.Add(new InstallerKeyUsageRecord { Id = Guid.NewGuid(), InstallerKeyId = key.Key.Id, TenantId = tenant, DeviceId = recent, RemoteAddress = "203.0.113.7", UsedAt = DateTimeOffset.UtcNow.AddDays(-29) });
      return await db.SaveChangesAsync();
    });

    var usages = (await admin.GetFromJsonAsync<InstallerKeyUsageList>(Usages(key.Key.Id, tenant), Cancel))!.Items;
    var count = Assert.Single(await ListAsync(admin, tenant)).UsageCount;
    var removed = await server.Services.GetRequiredService<InstallerKeyUsageCleanup>().CleanAsync(Cancel);

    var usage = Assert.Single(usages);
    Assert.Equal(recent, usage.DeviceId);
    Assert.Equal("203.0.113.7", usage.RemoteAddress);
    Assert.Equal(1, count);
    Assert.Equal(1, removed);
    Assert.Equal(recent, (await server.WithDbAsync(db => db.InstallerKeyUsages.SingleAsync())).DeviceId);
  }

  [Fact]
  public async Task HistoryOfZeroDays_KeepsEveryUsage()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("InstallerKeys:UsageHistoryDays", "0");
    var key = await TestInstallerKeys.CreateAsync(server, await TestTenants.EnsureAsync(server));
    await server.WithDbAsync(async db =>
    {
      db.InstallerKeyUsages.Add(new InstallerKeyUsageRecord { Id = Guid.NewGuid(), InstallerKeyId = TestInstallerKeys.IdOf(key), TenantId = await db.Tenants.Select(x => x.Id).SingleAsync(), UsedAt = DateTimeOffset.UtcNow.AddYears(-3) });
      return await db.SaveChangesAsync();
    });

    var removed = await server.Services.GetRequiredService<InstallerKeyUsageCleanup>().CleanAsync(Cancel);

    Assert.Equal(0, removed);
    Assert.True(await server.WithDbAsync(db => db.InstallerKeyUsages.AnyAsync()));
  }

  private static string All(Guid tenant) => $"{Routes.InstallerKeys}?tenantId={tenant}";

  private static string One(Guid id, Guid tenant) => $"{Routes.InstallerKey(id)}?tenantId={tenant}";

  private static string Usages(Guid id, Guid tenant) => $"{Routes.InstallerKeyUsages(id)}?tenantId={tenant}";

  private static async Task<CreatedInstallerKey> CreateAsync(HttpClient client, Guid tenant, CreateInstallerKeyRequest request)
  {
    var response = await client.PostAsJsonAsync(All(tenant), request, Cancel);
    Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Cancel));
    return (await response.Content.ReadFromJsonAsync<CreatedInstallerKey>(Cancel))!;
  }

  private static async Task<IReadOnlyList<InstallerKey>> ListAsync(HttpClient client, Guid tenant) =>
    (await client.GetFromJsonAsync<InstallerKeyList>(All(tenant), Cancel))!.Items;

  private static async Task<(HttpClient Client, Guid TenantId)> RegisterAsync(ServerHost server, string email)
  {
    using var anonymous = TestAccounts.Client(server);
    var response = await anonymous.PostAsJsonAsync($"{Routes.Auth}/register", new { email, password = TestAccounts.Password }, Cancel);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var client = await ClientForAsync(server, email);
    return (client, (await client.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel))!.TenantId);
  }

  private static async Task<HttpClient> ClientForAsync(ServerHost server, string email)
  {
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
    return client;
  }
}
