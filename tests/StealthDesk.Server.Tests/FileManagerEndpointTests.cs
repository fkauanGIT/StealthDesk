using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Realtime;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>A directory's contents, and a file's bytes, over HTTP: both refused without <c>device.file-manager.read</c>.</summary>
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

  [Fact]
  public async Task Download_WithoutThePermission_IsNotFound()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var deviceId = await ReportDeviceAsync(server);
    var client = await ClientForAsync(server, "nobody2@example.com", (_, _) => Task.CompletedTask);

    var response = await client.GetAsync(Routes.DeviceFileDownload(deviceId, @"C:\report.pdf"), Cancel);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task Download_AMissingFile_IsNotFound()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var agent = await TestAgent.ConnectAsync(server, callbacks: new FileDownloadAgent(content: null, "gone.txt"));
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var client = await ClientForAsync(server, "tech-dl1@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DeviceFileManagerRead, PermissionScopeKind.Tenant, user.TenantId));

    var response = await client.GetAsync(Routes.DeviceFileDownload(deviceId, @"C:\gone.txt"), Cancel);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task Download_AFileAboveTheLimit_IsTooLarge()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("FileManager:MaxDownloadSizeBytes", "2");
    var content = new byte[] { 1, 2, 3 };
    var callbacks = new FileDownloadAgent(content, "big.bin");
    await using var agent = await TestAgent.ConnectAsync(server, callbacks: callbacks);
    callbacks.Channel = agent.Channel;
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var client = await ClientForAsync(server, "tech-dl2@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DeviceFileManagerRead, PermissionScopeKind.Tenant, user.TenantId));

    var response = await client.GetAsync(Routes.DeviceFileDownload(deviceId, @"C:\big.bin"), Cancel);

    Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
  }

  [Fact]
  public async Task Download_AFileAboveTheLimit_DoesNotLeaveTheAgentsCallHanging()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("FileManager:MaxDownloadSizeBytes", "2");
    var content = RandomNumberGenerator.GetBytes(100);
    // Many chunks, so the sink's bounded channel (capacity 8) would fill up and block the agent's call forever if
    // the fix (cancelling a pending write once the request is done with the transfer) weren't in place.
    var callbacks = new FileDownloadAgent(content, "big.bin", chunkCount: 20);
    await using var agent = await TestAgent.ConnectAsync(server, callbacks: callbacks);
    callbacks.Channel = agent.Channel;
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var client = await ClientForAsync(server, "tech-dl4@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DeviceFileManagerRead, PermissionScopeKind.Tenant, user.TenantId));

    var response = await client.GetAsync(Routes.DeviceFileDownload(deviceId, @"C:\big.bin"), Cancel);

    Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    var completed = await Task.WhenAny(callbacks.Sent!, Task.Delay(TimeSpan.FromSeconds(10), Cancel));
    Assert.Same(callbacks.Sent, completed);
  }

  [Fact]
  public async Task Download_WithThePermission_StreamsTheAgentsBytes_WithItsDisplayName()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var content = RandomNumberGenerator.GetBytes(200_000); // past one chunk, to prove the stream isn't cut short
    var callbacks = new FileDownloadAgent(content, "report.pdf");
    await using var agent = await TestAgent.ConnectAsync(server, callbacks: callbacks);
    callbacks.Channel = agent.Channel;
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var client = await ClientForAsync(server, "tech-dl3@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DeviceFileManagerRead, PermissionScopeKind.Tenant, user.TenantId));

    var response = await client.GetAsync(Routes.DeviceFileDownload(deviceId, @"C:\report.pdf"), Cancel);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
    Assert.Contains("report.pdf", response.Content.Headers.ContentDisposition?.ToString());
    Assert.Equal(content, await response.Content.ReadAsByteArrayAsync(Cancel));
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

  /// <summary>
  /// An agent that answers a file download with the given bytes under the given name, split into two chunks sent
  /// over its real channel; <paramref name="content"/> null answers "doesn't exist" and sends nothing.
  /// </summary>
  private sealed class FileDownloadAgent(byte[]? content, string fileName, int chunkCount = 2) : RefusingAgent
  {
    /// <summary>Set after the test agent connects: the callbacks exist before its channel does.</summary>
    public IRealtimeChannel<IAgentGateway>? Channel { get; set; }

    /// <summary>The agent's own call sending the bytes, so a test can prove it finishes rather than hanging.</summary>
    public Task? Sent { get; private set; }

    public override Task<GatewayReply<FileDownloadInfo>> StartFileDownload(Guid transferId, string path, CancellationToken cancellationToken)
    {
      if (content is null)
      {
        return Task.FromResult(GatewayReply.Accept(new FileDownloadInfo(Exists: false, string.Empty, 0)));
      }

      Sent = Channel!.Server.SendFileChunks(transferId, Chunks(content, chunkCount));
      return Task.FromResult(GatewayReply.Accept(new FileDownloadInfo(Exists: true, fileName, content.Length)));
    }

    private static async IAsyncEnumerable<byte[]> Chunks(byte[] content, int chunkCount)
    {
      var size = Math.Max(1, content.Length / chunkCount);
      for (var offset = 0; offset < content.Length; offset += size)
      {
        yield return content[offset..Math.Min(offset + size, content.Length)];
        await Task.Yield();
      }
    }
  }

  /// <summary>An agent that answers the one listing it was given.</summary>
  private sealed class FileManagerAgent(DirectoryListing listing) : RefusingAgent
  {
    public override Task<GatewayReply<DirectoryListing>> ListDirectory(string path, CancellationToken cancellationToken) =>
      Task.FromResult(GatewayReply.Accept(listing));
  }
}
