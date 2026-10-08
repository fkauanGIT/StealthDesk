using System.Net;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>The device API shows each user only the devices they may read, with the filter running in PostgreSQL.</summary>
public class DevicePermissionTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task WithoutDeviceRead_TheListIsEmptyAndEveryDeviceIsNotFound()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var devices = await ReportDevicesAsync(server, 2);
    using var client = await ClientForAsync(server, "nobody@example.com", (_, _) => Task.CompletedTask);

    Assert.Empty(await ListAsync(client));
    foreach (var id in devices)
    {
      Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Routes.Device(id), Cancel)).StatusCode);
    }
  }

  [Fact]
  public async Task GrantedOneDevice_SeesOnlyThatDevice()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var devices = await ReportDevicesAsync(server, 3);
    using var client = await ClientForAsync(server, "tech@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DeviceRead, PermissionScopeKind.Device, devices[1]));

    Assert.Equal([devices[1]], await ListAsync(client));
    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Routes.Device(devices[1]), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Routes.Device(devices[0]), Cancel)).StatusCode);
  }

  [Fact]
  public async Task DenyOnADevice_RemovesIt_EvenWithTheTenantAllowed()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var devices = await ReportDevicesAsync(server, 3);
    using var client = await ClientForAsync(server, "tech@example.com", async (server, user) =>
    {
      await TestPermissions.AllowTenantDevicesAsync(server, user);
      await TestPermissions.AssignAsync(server, user, PermissionNames.DeviceRead, PermissionScopeKind.Device, devices[2], PermissionEffect.Deny);
    });

    Assert.Equal(devices[..2].Order(), (await ListAsync(client)).Order());
    Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Routes.Device(devices[2]), Cancel)).StatusCode);
  }

  [Fact]
  public async Task RemovingTheGrant_AppliesToTheNextRequest()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var devices = await ReportDevicesAsync(server, 1);
    using var client = await ClientForAsync(server, "tech@example.com", TestPermissions.AllowTenantDevicesAsync);
    Assert.Single(await ListAsync(client));

    await server.WithDbAsync(db => db.PermissionAssignments.Where(x => x.Permission == PermissionNames.DeviceRead).ExecuteDeleteAsync());

    Assert.Empty(await ListAsync(client));
    Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Routes.Device(devices[0]), Cancel)).StatusCode);
  }

  private static async Task<HttpClient> ClientForAsync(ServerHost server, string email, Func<ServerHost, UserRecord, Task> grant)
  {
    await grant(server, await TestAccounts.CreateUserAsync(server, email));
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
    return client;
  }

  private static async Task<Guid[]> ReportDevicesAsync(ServerHost server, int count)
  {
    var ids = new Guid[count];
    for (var i = 0; i < count; i++)
    {
      await using var agent = await TestAgent.ConnectAsync(server);
      ids[i] = Guid.NewGuid();
      var reply = await agent.ReportAsync(TestAgent.Report(ids[i], $"PC-{i}"));
      Assert.True(reply.Accepted, reply.Error);
    }

    return ids;
  }

  private static async Task<Guid[]> ListAsync(HttpClient client) =>
    [.. (await client.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, Cancel))!.Select(x => x.Id)];
}
