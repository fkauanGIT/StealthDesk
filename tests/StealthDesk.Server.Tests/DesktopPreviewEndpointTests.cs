using System.Net;
using StealthDesk.Contracts.DesktopClients;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>
/// A still picture of a device's current desktop, fetched as a plain image: refused without
/// <c>device.desktop-preview.read</c>, and for everyone once the deployment turns the feature off, regardless of
/// anyone's permission.
/// </summary>
public class DesktopPreviewEndpointTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task WithoutThePermission_IsNotFound()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var deviceId = await ReportDeviceAsync(server);
    var client = await ClientForAsync(server, "nobody@example.com", (_, _) => Task.CompletedTask);

    var response = await client.GetAsync(Routes.DeviceDesktopPreview(deviceId), Cancel);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task AnUnknownDevice_IsNotFound()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var client = await ClientForAsync(server, "tech@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DeviceDesktopPreviewRead, PermissionScopeKind.Tenant, user.TenantId));

    var response = await client.GetAsync(Routes.DeviceDesktopPreview(Guid.NewGuid()), Cancel);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task WithThePermission_TheAgentsPicture_ComesBackAsTheImage()
  {
    // A real screenshot's size, not a handful of bytes: the gateway's default 32 KB message limit silently drops the
    // agent's connection on anything bigger, so a tiny picture here would never catch that regression.
    var picture = new byte[150_000];
    Random.Shared.NextBytes(picture);
    using var server = await ServerHost.OnPostgresAsync();
    await using var agent = await TestAgent.ConnectAsync(server, callbacks: new PreviewAgent(new DesktopPreview(picture, ImageEncoding.Png)));
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var client = await ClientForAsync(server, "tech@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DeviceDesktopPreviewRead, PermissionScopeKind.Tenant, user.TenantId));

    var response = await client.GetAsync(Routes.DeviceDesktopPreview(deviceId), Cancel);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    Assert.Equal<byte[]>(picture, await response.Content.ReadAsByteArrayAsync(Cancel));
  }

  [Fact]
  public async Task WhenTheDeploymentSettingDisablesIt_EveryoneIsRefused_RegardlessOfPermission()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("RemoteControl:EnableDesktopPreview", "false");
    await using var agent = await TestAgent.ConnectAsync(server, callbacks: new PreviewAgent(new DesktopPreview([1], ImageEncoding.Jpeg)));
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var client = await ClientForAsync(server, "tech@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DeviceDesktopPreviewRead, PermissionScopeKind.Tenant, user.TenantId));

    var response = await client.GetAsync(Routes.DeviceDesktopPreview(deviceId), Cancel);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  private static async Task<Guid> ReportDeviceAsync(ServerHost server)
  {
    await using var agent = await TestAgent.ConnectAsync(server);
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    return deviceId;
  }

  private static async Task<HttpClient> ClientForAsync(ServerHost server, string email, Func<ServerHost, UserRecord, Task> grant)
  {
    await grant(server, await TestAccounts.CreateUserAsync(server, email));
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
    return client;
  }

  /// <summary>An agent that answers the one picture it was given.</summary>
  private sealed class PreviewAgent(DesktopPreview preview) : RefusingAgent
  {
    public override Task<GatewayReply<DesktopPreview>> GetDesktopPreview(CancellationToken cancellationToken) =>
      Task.FromResult(GatewayReply.Accept(preview));
  }
}
