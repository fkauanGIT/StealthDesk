using System.Net;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>Restarting or shutting down a device: refused without <c>device.power.manage</c>.</summary>
public class PowerActionEndpointTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task WithoutThePermission_IsNotFound()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var agent = await TestAgent.ConnectAsync(server);
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var client = await ClientForAsync(server, "nobody@example.com", (_, _) => Task.CompletedTask);

    var response = await client.PostAsync(Routes.DevicePower(deviceId, PowerAction.Restart), content: null, Cancel);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task AnUnknownDevice_IsNotFound()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var client = await ClientForAsync(server, "tech@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DevicePowerManage, PermissionScopeKind.Tenant, user.TenantId));

    var response = await client.PostAsync(Routes.DevicePower(Guid.NewGuid(), PowerAction.Shutdown), content: null, Cancel);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task WithThePermission_TheAgentIsAsked_ForTheRequestedAction()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var recorder = new PowerRecordingAgent();
    await using var agent = await TestAgent.ConnectAsync(server, callbacks: recorder);
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var client = await ClientForAsync(server, "tech@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DevicePowerManage, PermissionScopeKind.Tenant, user.TenantId));

    var response = await client.PostAsync(Routes.DevicePower(deviceId, PowerAction.Restart), content: null, Cancel);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal([PowerAction.Restart], recorder.Requested);
  }

  [Fact]
  public async Task WithThePermission_WhenTheAgentRefuses_IsAConflict()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var agent = await TestAgent.ConnectAsync(server); // a plain RefusingAgent refuses ChangePowerState
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var client = await ClientForAsync(server, "tech-refuse@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DevicePowerManage, PermissionScopeKind.Tenant, user.TenantId));

    var response = await client.PostAsync(Routes.DevicePower(deviceId, PowerAction.Restart), content: null, Cancel);

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
  }

  private static async Task<HttpClient> ClientForAsync(ServerHost server, string email, Func<ServerHost, UserRecord, Task> grant)
  {
    await grant(server, await TestAccounts.CreateUserAsync(server, email));
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
    return client;
  }

  /// <summary>An agent that accepts any power change and remembers what it was asked for.</summary>
  private sealed class PowerRecordingAgent : RefusingAgent
  {
    public List<PowerAction> Requested { get; } = [];

    public override Task<GatewayReply<bool>> ChangePowerState(PowerAction action, CancellationToken cancellationToken)
    {
      Requested.Add(action);
      return Task.FromResult(GatewayReply.Accept(true));
    }
  }
}
