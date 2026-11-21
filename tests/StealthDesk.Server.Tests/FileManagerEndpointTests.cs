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

  [Fact]
  public async Task Upload_WithoutThePermission_IsNotFound()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var deviceId = await ReportDeviceAsync(server);
    var client = await ClientForAsync(server, "nobody3@example.com", (_, _) => Task.CompletedTask);

    var response = await client.PostAsync(Routes.DeviceFileUpload(deviceId, @"C:\", "a.txt", overwrite: false), new ByteArrayContent([1, 2, 3]), Cancel);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task Upload_AboveTheDeclaredLimit_IsTooLarge_WithoutAskingTheDevice()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("FileManager:MaxUploadSizeBytes", "2");
    // A plain RefusingAgent would answer 409 if asked: a 413 here instead proves the size was checked first.
    await using var agent = await TestAgent.ConnectAsync(server);
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var client = await ClientForAsync(server, "tech-ul1@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DeviceFileManagerWrite, PermissionScopeKind.Tenant, user.TenantId));

    var response = await client.PostAsync(Routes.DeviceFileUpload(deviceId, @"C:\", "big.bin", overwrite: false), new ByteArrayContent([1, 2, 3]), Cancel);

    Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
  }

  [Fact]
  public async Task Upload_WhenTheAgentRefuses_IsConflict()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var agent = await TestAgent.ConnectAsync(server); // a plain RefusingAgent refuses StartFileUpload
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var client = await ClientForAsync(server, "tech-ul2@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DeviceFileManagerWrite, PermissionScopeKind.Tenant, user.TenantId));

    var response = await client.PostAsync(Routes.DeviceFileUpload(deviceId, @"C:\", "a.txt", overwrite: false), new ByteArrayContent([1, 2, 3]), Cancel);

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
  }

  [Fact]
  public async Task Upload_WithThePermission_WritesTheAgentsBytes_PulledOverTheRealChannel()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var callbacks = new FileUploadAgent();
    await using var agent = await TestAgent.ConnectAsync(server, callbacks: callbacks);
    callbacks.Channel = agent.Channel;
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var client = await ClientForAsync(server, "tech-ul3@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DeviceFileManagerWrite, PermissionScopeKind.Tenant, user.TenantId));
    var content = RandomNumberGenerator.GetBytes(200_000); // past one chunk, to prove more than one is pulled

    var response = await client.PostAsync(Routes.DeviceFileUpload(deviceId, @"C:\reports", "upload.bin", overwrite: false), new ByteArrayContent(content), Cancel);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("C:\\reports", callbacks.DirectoryPath);
    Assert.Equal("upload.bin", callbacks.FileName);
    Assert.Equal(content, callbacks.Received);
  }

  [Fact]
  public async Task Upload_WhenTheAgentFailsToSaveIt_IsBadGateway()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var callbacks = new FileUploadAgent { Failure = "The device couldn't save the file." };
    await using var agent = await TestAgent.ConnectAsync(server, callbacks: callbacks);
    callbacks.Channel = agent.Channel;
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var client = await ClientForAsync(server, "tech-ul4@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DeviceFileManagerWrite, PermissionScopeKind.Tenant, user.TenantId));

    var response = await client.PostAsync(Routes.DeviceFileUpload(deviceId, @"C:\", "a.txt", overwrite: false), new ByteArrayContent([1, 2, 3]), Cancel);

    Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
  }

  [Fact]
  public async Task Upload_WhenTheAgentNeverReportsBack_IsGatewayTimeout()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("Gateway:AgentCallTimeout", "00:00:01");
    var callbacks = new FileUploadAgent { ReportBack = false };
    await using var agent = await TestAgent.ConnectAsync(server, callbacks: callbacks);
    callbacks.Channel = agent.Channel;
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var client = await ClientForAsync(server, "tech-ul5@example.com", (server, user) =>
      TestPermissions.AssignAsync(server, user, PermissionNames.DeviceFileManagerWrite, PermissionScopeKind.Tenant, user.TenantId));

    var response = await client.PostAsync(Routes.DeviceFileUpload(deviceId, @"C:\", "a.txt", overwrite: false), new ByteArrayContent([1, 2, 3]), Cancel);

    Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
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

  /// <summary>
  /// An agent that accepts every upload, pulling its bytes over the real channel (proving the server's streaming
  /// hub method actually works, not just the accept/refuse round trip).
  /// </summary>
  private sealed class FileUploadAgent : RefusingAgent
  {
    /// <summary>Set after the test agent connects: the callbacks exist before its channel does.</summary>
    public IRealtimeChannel<IAgentGateway>? Channel { get; set; }

    /// <summary>Non-null answers <see cref="IAgentGateway.ReportUploadResult"/> with this instead of success.</summary>
    public string? Failure { get; set; }

    /// <summary>False skips reporting back at all, as if the agent died mid-upload.</summary>
    public bool ReportBack { get; set; } = true;

    public string? DirectoryPath { get; private set; }

    public string? FileName { get; private set; }

    /// <summary>The bytes received, once the pull finishes.</summary>
    public byte[]? Received { get; private set; }

    public override Task<GatewayReply<Guid>> StartFileUpload(Guid transferId, string directoryPath, string fileName, bool overwrite, CancellationToken cancellationToken)
    {
      DirectoryPath = directoryPath;
      FileName = fileName;
      _ = ReceiveAsync(transferId);
      return Task.FromResult(GatewayReply.Accept(transferId));
    }

    private async Task ReceiveAsync(Guid transferId)
    {
      using var buffer = new MemoryStream();
      await foreach (var chunk in Channel!.Server.ReadUploadChunks(transferId, CancellationToken.None))
      {
        buffer.Write(chunk);
      }

      Received = buffer.ToArray();
      if (ReportBack)
      {
        await Channel!.Server.ReportUploadResult(transferId, Failure);
      }
    }
  }
}
