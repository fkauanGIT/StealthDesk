using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StealthDesk.Agent.Core;
using StealthDesk.Agent.Core.Inventory;
using StealthDesk.Agent.Core.Settings;
using StealthDesk.Agent.Tests.Fakes;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Contracts.Terminal;
using StealthDesk.Realtime;

namespace StealthDesk.Agent.Tests;

/// <summary>What the agent answers when the server calls it, wired as the agent runs.</summary>
public class AgentCallbacksTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task TheAgentsCallbacks_AnswerWithItsVersion()
  {
    await using var services = Build(new FakeChannel(), out _);

    var reply = await services.GetRequiredService<IAgentCallbacks>().GetAgentVersion(Cancel);

    Assert.True(reply.Accepted, reply.Error);
    Assert.Equal(PortableInventory.AgentVersion, reply.Value);
  }

  [Fact]
  public async Task TheServersTerminalCalls_RunPowerShell_AndItsOutputGoesOverTheConnection()
  {
    var channel = new FakeChannel();
    await using var services = Build(channel, out var dataRoot);
    var agent = services.GetRequiredService<IAgentCallbacks>();
    var terminal = Guid.NewGuid();

    try
    {
      var opened = await agent.OpenTerminal(terminal, Cancel);
      Assert.True(opened.Accepted, opened.Error);
      Assert.Equal(terminal, opened.Value);
      await UntilAsync(channel, x => x.EndsWith("> ", StringComparison.Ordinal));

      var sent = await agent.SendTerminalInput(terminal, "'relay-' + (6 * 7)", Cancel);
      Assert.True(sent.Accepted, sent.Error);
      await UntilAsync(channel, x => x.Contains("relay-42", StringComparison.Ordinal));

      var closed = await agent.CloseTerminal(terminal, Cancel);
      Assert.True(closed.Accepted, closed.Error);
      await UntilAsync(channel, _ => Output(channel).Any(x => x is { Kind: TerminalOutputKind.Ended, Text: "The session was closed." }));

      Assert.All(Output(channel), x => Assert.Equal(terminal, x.TerminalId));
      // Gone on the agent: the server is told so and lets go of it too.
      Assert.Equal("The terminal session has ended.", (await agent.CloseTerminal(terminal, Cancel)).Error);
      Assert.Equal("The terminal session has ended.", (await agent.SendTerminalInput(terminal, "dir", Cancel)).Error);
    }
    finally
    {
      await services.DisposeAsync();
      TryDelete(dataRoot);
    }
  }

  [Fact]
  public async Task TheAgentsCallbacks_ListADirectory_WithWhatsInIt()
  {
    var channel = new FakeChannel();
    await using var services = Build(channel, out var dataRoot);
    try
    {
      Directory.CreateDirectory(dataRoot);
      await File.WriteAllTextAsync(Path.Combine(dataRoot, "a.txt"), "hi", Cancel);
      Directory.CreateDirectory(Path.Combine(dataRoot, "sub"));

      var reply = await services.GetRequiredService<IAgentCallbacks>().ListDirectory(dataRoot, Cancel);

      Assert.True(reply.Accepted, reply.Error);
      Assert.True(reply.Value!.Exists);
      Assert.Contains(reply.Value.Entries, x => x.Name == "a.txt" && !x.IsDirectory && x.SizeBytes == 2);
      Assert.Contains(reply.Value.Entries, x => x.Name == "sub" && x.IsDirectory);
    }
    finally
    {
      TryDelete(dataRoot);
    }
  }

  [Fact]
  public async Task TheAgentsCallbacks_DownloadsAFile_WithItsBytes()
  {
    var channel = new FakeChannel();
    await using var services = Build(channel, out var dataRoot);
    try
    {
      Directory.CreateDirectory(dataRoot);
      var filePath = Path.Combine(dataRoot, "report.pdf");
      var content = RandomNumberGenerator.GetBytes(200_000); // past one chunk, to prove more than one is sent
      await File.WriteAllBytesAsync(filePath, content, Cancel);
      var transferId = Guid.NewGuid();

      var reply = await services.GetRequiredService<IAgentCallbacks>().StartFileDownload(transferId, filePath, Cancel);

      Assert.True(reply.Accepted, reply.Error);
      Assert.True(reply.Value!.Exists);
      Assert.Equal("report.pdf", reply.Value.FileName);
      Assert.Equal(content.Length, reply.Value.SizeBytes);
      await UntilAsync(channel, () => channel.FileChunks.Any(x => x.TransferId == transferId));
      var sent = channel.FileChunks.Single(x => x.TransferId == transferId);
      Assert.Equal(content, sent.Bytes);
    }
    finally
    {
      TryDelete(dataRoot);
    }
  }

  [Fact]
  public async Task TheAgentsCallbacks_DownloadsAFolder_AsAZip_AndDeletesTheTempFileAfter()
  {
    var channel = new FakeChannel();
    await using var services = Build(channel, out var dataRoot);
    try
    {
      var folderPath = Path.Combine(dataRoot, "reports");
      Directory.CreateDirectory(folderPath);
      Directory.CreateDirectory(Path.Combine(folderPath, "sub"));
      await File.WriteAllTextAsync(Path.Combine(folderPath, "a.txt"), "hi", Cancel);
      await File.WriteAllTextAsync(Path.Combine(folderPath, "sub", "b.txt"), "there", Cancel);
      var transferId = Guid.NewGuid();
      var tempDir = Path.GetTempPath();
      var tempZipsBefore = Directory.EnumerateFiles(tempDir, "stealthdesk-download-*.zip").ToList();

      var reply = await services.GetRequiredService<IAgentCallbacks>().StartFileDownload(transferId, folderPath, Cancel);

      Assert.True(reply.Accepted, reply.Error);
      Assert.True(reply.Value!.Exists);
      Assert.Equal("reports.zip", reply.Value.FileName);
      Assert.True(reply.Value.SizeBytes > 0);
      await UntilAsync(channel, () => channel.FileChunks.Any(x => x.TransferId == transferId));
      var sent = channel.FileChunks.Single(x => x.TransferId == transferId);
      using var zip = new ZipArchive(new MemoryStream(sent.Bytes), ZipArchiveMode.Read);
      Assert.Equal(["a.txt", "sub/b.txt"], zip.Entries.Select(x => x.FullName).OrderBy(x => x));

      // Cleaned up rather than left behind: this is a temp file nobody else will ever read. Polled rather than
      // checked once: the delete happens right after SendFileChunks returns, not necessarily before this line runs.
      await UntilAsync(channel, () => Directory.EnumerateFiles(tempDir, "stealthdesk-download-*.zip").Count() == tempZipsBefore.Count);
    }
    finally
    {
      TryDelete(dataRoot);
    }
  }

  [Fact]
  public async Task TheAgentsCallbacks_AMissingFile_AnswersNotExists_AndSendsNothing()
  {
    var channel = new FakeChannel();
    await using var services = Build(channel, out var dataRoot);

    var reply = await services.GetRequiredService<IAgentCallbacks>().StartFileDownload(Guid.NewGuid(), Path.Combine(dataRoot, "gone.txt"), Cancel);

    Assert.True(reply.Accepted, reply.Error);
    Assert.False(reply.Value!.Exists);
    await Task.Delay(50, Cancel); // nothing to wait on: proving an absence needs a beat, not a condition
    Assert.Empty(channel.FileChunks);
  }

  [Fact]
  public async Task TheAgentsCallbacks_UploadsAFile_PulledFromTheServer_WithItsBytes()
  {
    var channel = new FakeChannel();
    await using var services = Build(channel, out var dataRoot);
    try
    {
      Directory.CreateDirectory(dataRoot);
      var content = RandomNumberGenerator.GetBytes(200_000); // past one chunk, to prove more than one is pulled
      var transferId = Guid.NewGuid();
      channel.UploadChunks = _ => Chunks(content, 3);

      var reply = await services.GetRequiredService<IAgentCallbacks>().StartFileUpload(transferId, dataRoot, "upload.bin", overwrite: false, Cancel);

      Assert.True(reply.Accepted, reply.Error);
      await UntilAsync(channel, () => channel.UploadResults.Any(x => x.TransferId == transferId));
      Assert.Null(channel.UploadResults.Single(x => x.TransferId == transferId).Error);
      Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dataRoot, "upload.bin"), Cancel));
    }
    finally
    {
      TryDelete(dataRoot);
    }
  }

  [Fact]
  public async Task TheAgentsCallbacks_UploadToAMissingFolder_IsRefused_AndPullsNoBytes()
  {
    var channel = new FakeChannel();
    await using var services = Build(channel, out var dataRoot);

    var reply = await services.GetRequiredService<IAgentCallbacks>().StartFileUpload(
      Guid.NewGuid(), Path.Combine(dataRoot, "gone"), "a.txt", overwrite: false, Cancel);

    Assert.False(reply.Accepted);
    await Task.Delay(50, Cancel); // nothing to wait on: proving an absence needs a beat, not a condition
    Assert.Empty(channel.UploadResults);
  }

  [Fact]
  public async Task TheAgentsCallbacks_UploadOverAnExistingFile_WithoutOverwrite_IsRefused_AndLeavesItAlone()
  {
    var channel = new FakeChannel();
    await using var services = Build(channel, out var dataRoot);
    try
    {
      Directory.CreateDirectory(dataRoot);
      var existingPath = Path.Combine(dataRoot, "a.txt");
      await File.WriteAllTextAsync(existingPath, "original", Cancel);

      var reply = await services.GetRequiredService<IAgentCallbacks>().StartFileUpload(Guid.NewGuid(), dataRoot, "a.txt", overwrite: false, Cancel);

      Assert.False(reply.Accepted);
      Assert.Equal("original", await File.ReadAllTextAsync(existingPath, Cancel));
    }
    finally
    {
      TryDelete(dataRoot);
    }
  }

  [Fact]
  public async Task TheAgentsCallbacks_UploadThatFailsMidStream_ReportsTheFailure()
  {
    var channel = new FakeChannel();
    await using var services = Build(channel, out var dataRoot);
    try
    {
      Directory.CreateDirectory(dataRoot);
      var transferId = Guid.NewGuid();
      channel.UploadChunks = _ => FailingChunks();

      var reply = await services.GetRequiredService<IAgentCallbacks>().StartFileUpload(transferId, dataRoot, "broken.bin", overwrite: false, Cancel);

      Assert.True(reply.Accepted, reply.Error);
      await UntilAsync(channel, () => channel.UploadResults.Any(x => x.TransferId == transferId));
      Assert.NotNull(channel.UploadResults.Single(x => x.TransferId == transferId).Error);
    }
    finally
    {
      TryDelete(dataRoot);
    }
  }

  [Fact]
  public async Task TheAgentsCallbacks_CreatesADirectory_InsideAnExistingFolder()
  {
    var channel = new FakeChannel();
    await using var services = Build(channel, out var dataRoot);
    try
    {
      Directory.CreateDirectory(dataRoot);

      var reply = await services.GetRequiredService<IAgentCallbacks>().CreateDirectory(dataRoot, "new folder", Cancel);

      Assert.True(reply.Accepted, reply.Error);
      Assert.Equal(Path.Combine(dataRoot, "new folder"), reply.Value);
      Assert.True(Directory.Exists(reply.Value));
    }
    finally
    {
      TryDelete(dataRoot);
    }
  }

  [Fact]
  public async Task TheAgentsCallbacks_CreatesADirectory_InAMissingParent_IsRefused()
  {
    var channel = new FakeChannel();
    await using var services = Build(channel, out var dataRoot);

    var reply = await services.GetRequiredService<IAgentCallbacks>().CreateDirectory(Path.Combine(dataRoot, "gone"), "new folder", Cancel);

    Assert.False(reply.Accepted);
  }

  [Fact]
  public async Task TheAgentsCallbacks_CreatesADirectory_OverSomethingThatAlreadyExists_IsRefused()
  {
    var channel = new FakeChannel();
    await using var services = Build(channel, out var dataRoot);
    try
    {
      Directory.CreateDirectory(dataRoot);
      await File.WriteAllTextAsync(Path.Combine(dataRoot, "taken"), "already here", Cancel);

      var reply = await services.GetRequiredService<IAgentCallbacks>().CreateDirectory(dataRoot, "taken", Cancel);

      Assert.False(reply.Accepted);
      Assert.Equal("already here", await File.ReadAllTextAsync(Path.Combine(dataRoot, "taken"), Cancel));
    }
    finally
    {
      TryDelete(dataRoot);
    }
  }

  [Fact]
  public async Task TheAgentsCallbacks_DeletesAFile()
  {
    var channel = new FakeChannel();
    await using var services = Build(channel, out var dataRoot);
    try
    {
      Directory.CreateDirectory(dataRoot);
      var filePath = Path.Combine(dataRoot, "gone.txt");
      await File.WriteAllTextAsync(filePath, "bye", Cancel);

      var reply = await services.GetRequiredService<IAgentCallbacks>().DeleteEntry(filePath, Cancel);

      Assert.True(reply.Accepted, reply.Error);
      Assert.False(File.Exists(filePath));
    }
    finally
    {
      TryDelete(dataRoot);
    }
  }

  [Fact]
  public async Task TheAgentsCallbacks_DeletesADirectory_WithEverythingInsideIt()
  {
    var channel = new FakeChannel();
    await using var services = Build(channel, out var dataRoot);
    try
    {
      var folderPath = Path.Combine(dataRoot, "folder");
      Directory.CreateDirectory(folderPath);
      await File.WriteAllTextAsync(Path.Combine(folderPath, "inside.txt"), "content", Cancel);

      var reply = await services.GetRequiredService<IAgentCallbacks>().DeleteEntry(folderPath, Cancel);

      Assert.True(reply.Accepted, reply.Error);
      Assert.False(Directory.Exists(folderPath));
    }
    finally
    {
      TryDelete(dataRoot);
    }
  }

  [Fact]
  public async Task TheAgentsCallbacks_DeletesSomethingMissing_IsRefused()
  {
    var channel = new FakeChannel();
    await using var services = Build(channel, out var dataRoot);

    var reply = await services.GetRequiredService<IAgentCallbacks>().DeleteEntry(Path.Combine(dataRoot, "gone.txt"), Cancel);

    Assert.False(reply.Accepted);
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

  private static async IAsyncEnumerable<byte[]> FailingChunks()
  {
    yield return [1, 2, 3];
    await Task.Yield();
    throw new IOException("Simulated failure partway through the upload.");
  }

  [Fact]
  public async Task AHostDisposedWithoutWaiting_EndsItsTerminals_WithoutThrowing()
  {
    var channel = new FakeChannel();
    var services = Build(channel, out var dataRoot);
    try
    {
      var opened = await services.GetRequiredService<IAgentCallbacks>().OpenTerminal(Guid.NewGuid(), Cancel);
      Assert.True(opened.Accepted, opened.Error);

      // Disposed as the command that joins a device to a server does.
      services.Dispose();

      await UntilAsync(channel, _ => Output(channel).Any(x => x.Kind == TerminalOutputKind.Ended));
    }
    finally
    {
      TryDelete(dataRoot);
    }
  }

  // The agent's own services, talking to a fake server, keeping its files in a folder of the test's.
  private static ServiceProvider Build(FakeChannel channel, out string dataRoot)
  {
    dataRoot = Path.Combine(Path.GetTempPath(), $"sd-callbacks-{Guid.NewGuid():N}");
    var builder = Host.CreateApplicationBuilder();
    builder.AddStealthDeskAgent(new AgentStartup("tests", new Uri("http://localhost:5099"), UseSettingsFile: false));
    builder.Services.AddSingleton<IAgentPaths>(new AgentPaths("tests", isDebugBuild: true, dataRoot));
    builder.Services.AddSingleton<IRealtimeChannel<IAgentGateway>>(channel);
    return builder.Services.BuildServiceProvider();
  }

  private static IReadOnlyList<TerminalOutput> Output(FakeChannel channel)
  {
    lock (channel.TerminalOutput)
    {
      return [.. channel.TerminalOutput];
    }
  }

  private static async Task UntilAsync(FakeChannel channel, Func<string, bool> shown)
  {
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
    string Screen() => string.Concat(Output(channel).Where(x => x.Kind != TerminalOutputKind.Ended).Select(x => x.Text));
    while (!shown(Screen()))
    {
      Assert.True(DateTime.UtcNow < deadline, $"Waited for the terminal; it shows:\n{Screen()}");
      await Task.Delay(20, Cancel);
    }
  }

  private static async Task UntilAsync(FakeChannel channel, Func<bool> condition)
  {
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
    while (!condition())
    {
      Assert.True(DateTime.UtcNow < deadline, "Waited for the file transfer, which never arrived.");
      await Task.Delay(20, Cancel);
    }
  }

  private static void TryDelete(string folder)
  {
    try
    {
      Directory.Delete(folder, recursive: true);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
  }
}
