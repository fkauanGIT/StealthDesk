using System.Net;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>Devices and their live updates belong to a tenant: only its signed-in users reach them.</summary>
public class TenantIsolationTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task DeviceApi_RequiresSignIn()
  {
    using var server = ServerHost.InMemory();
    using var client = TestAccounts.Client(server);

    var list = await client.GetAsync(Routes.Devices, Cancel);
    var one = await client.GetAsync(Routes.Device(Guid.NewGuid()), Cancel);

    Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, one.StatusCode);
  }

  [Fact]
  public async Task Dashboard_RefusesConnectionsWithoutSignIn()
  {
    using var server = ServerHost.InMemory();
    await using var connection = new HubConnectionBuilder()
      .WithUrl(new Uri(server.Server.BaseAddress, Routes.Dashboard), options =>
      {
        options.HttpMessageHandlerFactory = _ => server.Server.CreateHandler();
        options.Transports = HttpTransportType.LongPolling;
      })
      .Build();

    var error = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync(Cancel));

    Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
  }

  [Fact]
  public async Task AgentsAndHealthChecks_StayOpen()
  {
    using var server = ServerHost.InMemory();
    using var client = TestAccounts.Client(server);
    await using var agent = await TestAgent.ConnectAsync(server);

    var reply = await agent.ReportAsync(TestAgent.Report(Guid.NewGuid()));

    Assert.True(reply.Accepted, reply.Error);
    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Routes.ServerVersion, Cancel)).StatusCode);
  }

  [Fact]
  public async Task Users_SeeOnlyTheirTenantsDevices()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var deviceId = await RegisterDeviceInFirstTenantAsync(server);
    var otherTenant = await TestTenants.CreateAsync(server, "Other");
    // The outsider reads every device of their own tenant, so seeing none here is the tenant boundary at work.
    await TestPermissions.AllowTenantDevicesAsync(server, await TestAccounts.CreateUserAsync(server, "outsider@example.com", otherTenant));
    using var outsider = TestAccounts.Client(server);
    outsider.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, "outsider@example.com"));
    using var member = await TestAccounts.SignedInClientAsync(server);

    var outsiderList = await outsider.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, Cancel);
    var outsiderOne = await outsider.GetAsync(Routes.Device(deviceId), Cancel);
    var memberList = await member.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices, Cancel);
    var memberOne = await member.GetAsync(Routes.Device(deviceId), Cancel);

    Assert.Empty(outsiderList!);
    Assert.Equal(HttpStatusCode.NotFound, outsiderOne.StatusCode);
    Assert.Equal(deviceId, Assert.Single(memberList!).Id);
    Assert.Equal(HttpStatusCode.OK, memberOne.StatusCode);
  }

  [Fact]
  public async Task LiveUpdates_ReachOnlyTheDevicesTenant()
  {
    using var server = ServerHost.InMemory();
    await using var agent = await TestAgent.ConnectAsync(server);
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);

    var otherTenant = await TestTenants.CreateAsync(server, "Other");
    // The outsider reads every device of their own tenant, so seeing none here is the tenant boundary at work.
    await TestPermissions.AllowTenantDevicesAsync(server, await TestAccounts.CreateUserAsync(server, "outsider@example.com", otherTenant));
    await using var outsider = await TestDashboard.ConnectAsync(server, await TestAccounts.SignInForCookieAsync(server, "outsider@example.com"));
    await using var member = await TestDashboard.ConnectSignedInAsync(server);

    // The outsider asks for the device by id; the server leaves it out.
    Assert.Empty(await outsider.SubscribeAsync(deviceId));
    Assert.Equal([deviceId], await member.SubscribeAsync(deviceId));

    await agent.ReportAsync(TestAgent.Report(deviceId) with { CpuLoad = 0.5 });

    Assert.Equal(deviceId, (await member.NextChangeAsync()).Id);
    await Assert.ThrowsAsync<TimeoutException>(() => outsider.NextChangeAsync(within: TimeSpan.FromSeconds(1)));
  }

  // Self-registration needs a single-tenant server, so the device joins before any other tenant exists.
  private static async Task<Guid> RegisterDeviceInFirstTenantAsync(ServerHost server)
  {
    await using var agent = await TestAgent.ConnectAsync(server);
    var deviceId = Guid.NewGuid();
    var reply = await agent.ReportAsync(TestAgent.Report(deviceId));
    Assert.True(reply.Accepted, reply.Error);
    return deviceId;
  }
}
