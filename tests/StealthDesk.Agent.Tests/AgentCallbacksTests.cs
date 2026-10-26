using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StealthDesk.Agent.Core;
using StealthDesk.Agent.Core.Inventory;
using StealthDesk.Contracts.Realtime;

namespace StealthDesk.Agent.Tests;

/// <summary>What the agent answers when the server calls it.</summary>
public class AgentCallbacksTests
{
  [Fact]
  public async Task TheAgentsCallbacks_AnswerWithItsVersion()
  {
    var builder = Host.CreateApplicationBuilder();
    builder.AddStealthDeskAgent(new AgentStartup("tests", new Uri("http://localhost:5099"), UseSettingsFile: false));
    using var services = builder.Services.BuildServiceProvider();

    var reply = await services.GetRequiredService<IAgentCallbacks>().GetAgentVersion(TestContext.Current.CancellationToken);

    Assert.True(reply.Accepted, reply.Error);
    Assert.Equal(PortableInventory.AgentVersion, reply.Value);
  }
}
