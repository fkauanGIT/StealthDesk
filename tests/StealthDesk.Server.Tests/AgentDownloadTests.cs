using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.TenantSettings;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>
/// The Windows agent the server hands out: anyone may download it, since the machine being installed has no account,
/// and the install options name its hash, so a command can refuse anything else.
/// </summary>
public sealed class AgentDownloadTests : IDisposable
{
  private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"sd-downloads-{Guid.NewGuid():N}")).FullName;

  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  private string AgentFile => Path.Combine(_folder, "windows", "StealthDesk.Agent.zip");

  public void Dispose() => Directory.Delete(_folder, recursive: true);

  [Fact]
  public async Task WithoutABuild_TheOptionsNameNone_AndThereIsNothingToDownload()
  {
    await using var server = ServerHost.InMemory().With("AgentDownloads:Folder", _folder);
    using var client = await SignedInAsync(server);
    using var anonymous = TestAccounts.Client(server);

    Assert.Null((await OptionsAsync(client)).WindowsAgent);
    Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(Routes.WindowsAgentDownload, Cancel)).StatusCode);
  }

  [Fact]
  public async Task WithABuild_TheOptionsNameItsHash_AndAnyoneDownloadsThoseBytes()
  {
    var bytes = Write(RandomNumberGenerator.GetBytes(300_000));
    await using var server = ServerHost.InMemory().With("AgentDownloads:Folder", _folder);
    using var client = await SignedInAsync(server);
    using var anonymous = TestAccounts.Client(server);

    var agent = (await OptionsAsync(client)).WindowsAgent;
    using var download = await anonymous.GetAsync(Routes.WindowsAgentDownload, Cancel);

    Assert.NotNull(agent);
    Assert.Equal(Routes.WindowsAgentDownload, agent.Path);
    Assert.Equal("StealthDesk.Agent.zip", agent.FileName);
    Assert.Equal(bytes.Length, agent.Size);
    Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), agent.Sha256);
    Assert.Equal(HttpStatusCode.OK, download.StatusCode);
    Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync(Cancel));
    Assert.Equal("StealthDesk.Agent.zip", download.Content.Headers.ContentDisposition?.FileName);
    Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
  }

  [Fact]
  public async Task ReplacingTheBuild_ChangesTheHashTheOptionsName()
  {
    Write([1, 2, 3]);
    await using var server = ServerHost.InMemory().With("AgentDownloads:Folder", _folder);
    using var client = await SignedInAsync(server);
    var before = (await OptionsAsync(client)).WindowsAgent!.Sha256;

    var newer = Write([4, 5, 6, 7]);
    File.SetLastWriteTimeUtc(AgentFile, DateTime.UtcNow.AddMinutes(1));

    Assert.NotEqual(before, (await OptionsAsync(client)).WindowsAgent!.Sha256);
    Assert.Equal(Convert.ToHexString(SHA256.HashData(newer)), (await OptionsAsync(client)).WindowsAgent!.Sha256);
  }

  [Fact]
  public async Task ARelativeFolder_IsTakenFromTheServersFolder()
  {
    await using var server = ServerHost.InMemory();

    var catalog = server.Services.GetRequiredService<StealthDesk.Web.Server.AgentDownloads.AgentDownloadCatalog>();

    Assert.Equal(
      Path.Combine(server.Services.GetRequiredService<IHostEnvironment>().ContentRootPath, "downloads", "windows", "StealthDesk.Agent.zip"),
      catalog.WindowsAgentPath);
  }

  private byte[] Write(byte[] bytes)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(AgentFile)!);
    File.WriteAllBytes(AgentFile, bytes);
    return bytes;
  }

  private static async Task<HttpClient> SignedInAsync(ServerHost server)
  {
    const string email = "admin@example.com";
    using var anonymous = TestAccounts.Client(server);
    var registered = await anonymous.PostAsJsonAsync($"{Routes.Auth}/register", new { email, password = TestAccounts.Password }, Cancel);
    Assert.Equal(HttpStatusCode.OK, registered.StatusCode);

    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
    return client;
  }

  private static async Task<DeploymentOptions> OptionsAsync(HttpClient client) =>
    (await client.GetFromJsonAsync<DeploymentOptions>(Routes.DeploymentOptions, Cancel))!;
}
