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
