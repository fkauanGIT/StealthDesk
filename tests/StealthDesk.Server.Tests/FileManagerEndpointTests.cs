using System.Net;
using System.Net.Http.Json;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>A directory's contents, fetched as JSON: refused without <c>device.file-manager.read</c>.</summary>
public class FileManagerEndpointTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task WithoutThePermission_IsNotFound()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var deviceId = await ReportDeviceAsync(server);
    var client = await ClientForAsync(server, "nobody@example.com", (_, _) => Task.CompletedTask);

    var response = await client.GetAsync(Routes.DeviceFiles(deviceId, string.Empty), Cancel);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task AnUnknownDevice_IsNotFound()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var client = await ClientForAsync(server, "tech@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DeviceFileManagerRead, PermissionScopeKind.Tenant, user.TenantId));

    var response = await client.GetAsync(Routes.DeviceFiles(Guid.NewGuid(), string.Empty), Cancel);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task WithThePermission_TheAgentsListing_ComesBackAsJson()
  {
    var listing = new DirectoryListing(@"C:\Reports", Exists: true,
      [new FileSystemEntry("a.txt", IsDirectory: false, SizeBytes: 123, DateTimeOffset.UtcNow)]);
    using var server = await ServerHost.OnPostgresAsync();
    await using var agent = await TestAgent.ConnectAsync(server, callbacks: new FileManagerAgent(listing));
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var client = await ClientForAsync(server, "tech@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DeviceFileManagerRead, PermissionScopeKind.Tenant, user.TenantId));

    var response = await client.GetAsync(Routes.DeviceFiles(deviceId, @"C:\Reports"), Cancel);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var body = await response.Content.ReadFromJsonAsync<DirectoryListing>(Cancel);
    Assert.Equivalent(listing, body);
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

  /// <summary>An agent that answers the one listing it was given.</summary>
  private sealed class FileManagerAgent(DirectoryListing listing) : RefusingAgent
  {
    public override Task<GatewayReply<DirectoryListing>> ListDirectory(string path, CancellationToken cancellationToken) =>
      Task.FromResult(GatewayReply.Accept(listing));
  }
}
