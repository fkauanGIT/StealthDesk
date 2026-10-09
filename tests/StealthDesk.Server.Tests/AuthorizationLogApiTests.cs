using System.Net;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>Reading the authorization change log: who may read which tenant, filters, search and paging.</summary>
public class AuthorizationLogApiTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task TenantAdministrator_ReadsTheirTenant_AndNoOther()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (_, firstTenant) = await RegisterAsync(server, "first@example.com");
    var (second, secondTenant) = await RegisterAsync(server, "second@example.com");

    var own = await second.GetAsync(Logs(secondTenant), Cancel);
    var other = await second.GetAsync(Logs(firstTenant), Cancel);
    var serverEntries = await second.GetAsync(Routes.ServerAuthorizationLogs, Cancel);

    Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    var page = (await own.Content.ReadFromJsonAsync<AuthorizationChangePage>(Cancel))!;
    var entry = Assert.Single(page.Items);
    Assert.Equal(secondTenant, entry.OwningTenantId);
    Assert.Equal(AuthorizationChangeActions.PermissionAssignmentsSeeded, entry.Action);
    Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, serverEntries.StatusCode);
  }

  [Fact]
  public async Task ServerAdministrator_PicksAnyTenant_AndReadsTheServerEntries()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (first, _) = await RegisterAsync(server, "first@example.com");
    var (_, secondTenant) = await RegisterAsync(server, "second@example.com");
    var serverEntry = (await AddAsync(server, null, Entry()))[0];

    var tenant = await PageAsync(first, Logs(secondTenant));
    var serverWide = await PageAsync(first, Routes.ServerAuthorizationLogs);

    Assert.Equal(secondTenant, Assert.Single(tenant.Items).OwningTenantId);
    Assert.Equal(serverEntry, Assert.Single(serverWide.Items).Id);
  }

  [Fact]
  public async Task WithoutEitherPermission_IsForbidden_EvenForTheirOwnTenant()
  {
    using var server = ServerHost.InMemory();
    var user = await TestAccounts.CreateUserAsync(server, "nobody@example.com");
    using var client = await ClientForAsync(server, "nobody@example.com");

    var response = await client.GetAsync(Logs(user.TenantId), Cancel);

    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
  }

  [Fact]
  public async Task Filters_ApplyTogether_NewestFirst()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var now = DateTimeOffset.UtcNow;
    var ids = await AddAsync(
      server,
      tenant,
      Entry("granted", AuthorizationChangeActors.User, "PermissionAssignment", now.AddDays(-3)),
      Entry("granted", AuthorizationChangeActors.User, "PermissionAssignment", now.AddDays(-1)),
      Entry("granted", AuthorizationChangeActors.System, "PermissionAssignment", now.AddDays(-1)),
      Entry("revoked", AuthorizationChangeActors.User, "PermissionAssignment", now.AddDays(-1)),
      Entry("granted", AuthorizationChangeActors.User, "UserGroup", now.AddDays(-1)),
      Entry("granted", AuthorizationChangeActors.User, "PermissionAssignment", now));

    var page = await PageAsync(admin, Logs(tenant, $"&action=granted&actorKind=user&targetKind=PermissionAssignment&from={Uri.EscapeDataString(now.AddDays(-2).ToString("O"))}"));

    Assert.Equal(2, page.TotalItems);
    Assert.Equal([ids[5], ids[1]], page.Items.Select(x => x.Id));
  }

  [Fact]
  public async Task Search_FindsWholeAndPartialIds_AndTakesWildcardsLiterally()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var target = Guid.Parse("3f2c9a1e-7b4d-4e8a-9c01-5d6e7f8a9b0c");
    var actor = Guid.Parse("a1b2c3d4-0000-4000-8000-000000000001");
    var ids = await AddAsync(server, tenant, Entry(targetId: target), Entry(actorId: actor));

    Assert.Equal([ids[0]], (await PageAsync(admin, Logs(tenant, $"&search={target}"))).Items.Select(x => x.Id));
    Assert.Equal([ids[0]], (await PageAsync(admin, Logs(tenant, "&search=7B4D-4E8A"))).Items.Select(x => x.Id));
    Assert.Equal([ids[1]], (await PageAsync(admin, Logs(tenant, "&search=c3d4-0000"))).Items.Select(x => x.Id));
    Assert.Empty((await PageAsync(admin, Logs(tenant, "&search=%25"))).Items);
    Assert.Empty((await PageAsync(admin, Logs(tenant, "&search=_"))).Items);

    var tooLong = await admin.GetAsync(Logs(tenant, $"&search={new string('a', AuthorizationChangeQuery.SearchMax + 1)}"), Cancel);
    Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
  }

  [Theory]
  [InlineData(0, 2, 2)]
  [InlineData(1, 2, 1)]
  [InlineData(5, 2, 0)]
  [InlineData(0, 1000, 3)]
  [InlineData(0, 0, 1)]
  [InlineData(int.MaxValue, 100, 0)]
  public async Task Paging_IsClampedToSafeValues(int pageNumber, int pageSize, int expected)
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    await AddAsync(server, tenant, Entry(), Entry());

    var page = await PageAsync(admin, Logs(tenant, $"&page={pageNumber}&pageSize={pageSize}"));

    Assert.Equal(3, page.TotalItems);
    Assert.Equal(expected, page.Items.Count);
  }

  [Fact]
  public async Task PageSize_NeverExceedsTheMaximum()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    await AddAsync(server, tenant, [.. Enumerable.Range(0, 120).Select(_ => Entry())]);

    var page = await PageAsync(admin, Logs(tenant, "&pageSize=1000"));

    Assert.Equal(121, page.TotalItems);
    Assert.Equal(AuthorizationChangeQuery.MaxPageSize, page.Items.Count);
  }

  [Fact]
  public async Task CurrentUser_ListsThePermissionsHeldOnTheTenantAndServer()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (first, _) = await RegisterAsync(server, "first@example.com");
    var (second, _) = await RegisterAsync(server, "second@example.com");
    var plain = await TestAccounts.CreateUserAsync(server, "plain@example.com");
    await TestPermissions.AssignAsync(server, plain, PermissionNames.DeviceRead, PermissionScopeKind.Device, Guid.NewGuid());
    using var plainClient = await ClientForAsync(server, "plain@example.com");

    var firstHeld = (await MeAsync(first)).Permissions;
    var secondHeld = (await MeAsync(second)).Permissions;
    var plainHeld = (await MeAsync(plainClient)).Permissions;

    Assert.Contains(PermissionNames.ServerAuthorizationLogsRead, firstHeld);
    Assert.Contains(PermissionNames.TenantAuthorizationLogsRead, firstHeld);
    Assert.DoesNotContain(PermissionNames.ServerAuthorizationLogsRead, secondHeld);
    Assert.Contains(PermissionNames.TenantAuthorizationLogsRead, secondHeld);
    // A grant on one device answers for that device only, not for the tenant.
    Assert.Empty(plainHeld);
  }

  private static string Logs(Guid tenantId, string filters = "") => $"{Routes.AuthorizationLogs}?tenantId={tenantId}{filters}";

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

  private static async Task<CurrentUser> MeAsync(HttpClient client) =>
    (await client.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel))!;

  private static async Task<AuthorizationChangePage> PageAsync(HttpClient client, string path)
  {
    var response = await client.GetAsync(path, Cancel);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<AuthorizationChangePage>(Cancel))!;
  }

  private static AuthorizationChangeRecord Entry(
    string action = "action",
    string actorKind = AuthorizationChangeActors.User,
    string targetKind = "Target",
    DateTimeOffset? createdAt = null,
    Guid? actorId = null,
    Guid? targetId = null) => new()
  {
    Id = Guid.NewGuid(),
    Action = action,
    ActorKind = actorKind,
    ActorId = actorId,
    TargetKind = targetKind,
    TargetId = targetId,
    CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
  };

  private static Task<Guid[]> AddAsync(ServerHost server, Guid? tenantId, params AuthorizationChangeRecord[] entries) => server.WithDbAsync(async db =>
  {
    foreach (var entry in entries)
    {
      entry.OwningTenantId = tenantId;
    }

    db.AuthorizationChanges.AddRange(entries);
    await db.SaveChangesAsync();
    return entries.Select(x => x.Id).ToArray();
  });
}
