using System.Net;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>Waking an offline device over the network: refused without <c>device.wake.send</c>.</summary>
public class WakeEndpointTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;
  private const string SharedIp = "203.0.113.9";
  private const string TargetMac = "00155D012345";

  [Fact]
  public async Task WithoutThePermission_IsNotFound()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var user = await TestAccounts.CreateUserAsync(server, "nobody@example.com");
    var deviceId = await OfflineTargetAsync(server, user.TenantId);
    var client = await SignedInAsync(server, user.Email!);

    var response = await client.PostAsync(Routes.DeviceWake(deviceId), content: null, Cancel);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task AnUnknownDevice_IsNotFound()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var user = await TestAccounts.CreateUserAsync(server, "tech@example.com");
    await TestPermissions.AssignAsync(server, user, PermissionNames.DeviceWakeSend, PermissionScopeKind.Tenant, user.TenantId);
    var client = await SignedInAsync(server, user.Email!);

    var response = await client.PostAsync(Routes.DeviceWake(Guid.NewGuid()), content: null, Cancel);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task WithoutAKnownMacAddress_IsAConflict()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var user = await TestAccounts.CreateUserAsync(server, "tech@example.com");
    await TestPermissions.AssignAsync(server, user, PermissionNames.DeviceWakeSend, PermissionScopeKind.Tenant, user.TenantId);
    var deviceId = Guid.NewGuid();
    await server.WithDbAsync(db =>
    {
      db.Devices.Add(new DeviceRecord { Id = deviceId, TenantId = user.TenantId, Name = "NO-MAC", PublicIpV4 = SharedIp });
      return db.SaveChangesAsync();
    });
    var client = await SignedInAsync(server, user.Email!);

    var response = await client.PostAsync(Routes.DeviceWake(deviceId), content: null, Cancel);

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
  }

  [Fact]
  public async Task WithoutAKnownPublicAddress_IsAConflict()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var user = await TestAccounts.CreateUserAsync(server, "tech@example.com");
    await TestPermissions.AssignAsync(server, user, PermissionNames.DeviceWakeSend, PermissionScopeKind.Tenant, user.TenantId);
    var deviceId = Guid.NewGuid();
    await server.WithDbAsync(db =>
    {
      db.Devices.Add(new DeviceRecord { Id = deviceId, TenantId = user.TenantId, Name = "NO-IP", MacAddresses = [TargetMac] });
      return db.SaveChangesAsync();
    });
    var client = await SignedInAsync(server, user.Email!);

    var response = await client.PostAsync(Routes.DeviceWake(deviceId), content: null, Cancel);

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
  }

  [Fact]
  public async Task WithNoOnlineDeviceOnTheSameNetwork_IsAConflict()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var user = await TestAccounts.CreateUserAsync(server, "tech@example.com");
    await TestPermissions.AssignAsync(server, user, PermissionNames.DeviceWakeSend, PermissionScopeKind.Tenant, user.TenantId);
    var deviceId = await OfflineTargetAsync(server, user.TenantId);
    var client = await SignedInAsync(server, user.Email!);

    var response = await client.PostAsync(Routes.DeviceWake(deviceId), content: null, Cancel);

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
  }

  [Fact]
  public async Task WithAnOnlineNeighbor_AsksItToBroadcastTheTargetsMacAddress()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var user = await TestAccounts.CreateUserAsync(server, "tech@example.com");
    await TestPermissions.AssignAsync(server, user, PermissionNames.DeviceWakeSend, PermissionScopeKind.Tenant, user.TenantId);
    var deviceId = await OfflineTargetAsync(server, user.TenantId);
    var recorder = new WakeRecordingAgent();
    var neighborId = Guid.NewGuid();
    await using var neighbor = await TestAgent.ConnectAsync(server, callbacks: recorder);
    Assert.True((await neighbor.ReportAsync(TestAgent.Report(neighborId, "NEIGHBOR-PC"))).Accepted);
    await server.WithDbAsync(async db =>
    {
      var device = await db.Devices.FirstAsync(x => x.Id == neighborId);
      device.TenantId = user.TenantId;
      device.PublicIpV4 = SharedIp;
      return await db.SaveChangesAsync();
    });
    var client = await SignedInAsync(server, user.Email!);

    var response = await client.PostAsync(Routes.DeviceWake(deviceId), content: null, Cancel);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal([TargetMac], recorder.Requested);
  }

  [Fact]
  public async Task WhenEveryNeighborRefuses_IsAConflict()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var user = await TestAccounts.CreateUserAsync(server, "tech@example.com");
    await TestPermissions.AssignAsync(server, user, PermissionNames.DeviceWakeSend, PermissionScopeKind.Tenant, user.TenantId);
    var deviceId = await OfflineTargetAsync(server, user.TenantId);
    var neighborId = Guid.NewGuid();
    // A plain RefusingAgent refuses SendWakePacket, same as it refuses everything else.
    await using var neighbor = await TestAgent.ConnectAsync(server);
    Assert.True((await neighbor.ReportAsync(TestAgent.Report(neighborId, "NEIGHBOR-PC"))).Accepted);
    await server.WithDbAsync(async db =>
    {
      var device = await db.Devices.FirstAsync(x => x.Id == neighborId);
      device.TenantId = user.TenantId;
      device.PublicIpV4 = SharedIp;
      return await db.SaveChangesAsync();
    });
    var client = await SignedInAsync(server, user.Email!);

    var response = await client.PostAsync(Routes.DeviceWake(deviceId), content: null, Cancel);

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
  }

  private static async Task<Guid> OfflineTargetAsync(ServerHost server, Guid tenant)
  {
    var deviceId = Guid.NewGuid();
    await server.WithDbAsync(db =>
    {
      db.Devices.Add(new DeviceRecord { Id = deviceId, TenantId = tenant, Name = "TARGET-PC", MacAddresses = [TargetMac], PublicIpV4 = SharedIp });
      return db.SaveChangesAsync();
    });

    return deviceId;
  }

  private static async Task<HttpClient> SignedInAsync(ServerHost server, string email)
  {
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
    return client;
  }

  /// <summary>An agent that accepts any wake packet and remembers the MAC address it was asked to broadcast.</summary>
  private sealed class WakeRecordingAgent : RefusingAgent
  {
    public List<string> Requested { get; } = [];

    public override Task<GatewayReply<bool>> SendWakePacket(string macAddress, CancellationToken cancellationToken)
    {
      Requested.Add(macAddress);
      return Task.FromResult(GatewayReply.Accept(true));
    }
  }
}
