using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StealthDesk.Agent.Core;
using StealthDesk.Agent.Core.Settings;

namespace StealthDesk.Agent.Tests;

/// <summary>What the command line hands the agent: the installer key and the id a new device asks for.</summary>
public class AgentStartupTests
{
  [Fact]
  public void InstallerKeyAndDeviceId_FromTheCommandLine_ReachTheSettings()
  {
    var deviceId = Guid.NewGuid();

    var settings = Settings(new AgentStartup("tests", new Uri("http://localhost:5099"), UseSettingsFile: false, InstallerKey: " key-id:secret ", DeviceId: deviceId));

    Assert.Equal("key-id:secret", settings.InstallerKey);
    Assert.Equal(deviceId, settings.DeviceId);
  }

  [Fact]
  public void WithoutThem_TheDeviceHasNoKeyAndWaitsForTheServersId()
  {
    var settings = Settings(new AgentStartup("tests", new Uri("http://localhost:5099"), UseSettingsFile: false));

    Assert.Null(settings.InstallerKey);
    Assert.Equal(Guid.Empty, settings.DeviceId);
  }

  private static AgentSettings Settings(AgentStartup startup)
  {
    var builder = Host.CreateApplicationBuilder();
    builder.AddStealthDeskAgent(startup);
    using var services = builder.Services.BuildServiceProvider();
    return services.GetRequiredService<IOptions<AgentSettings>>().Value;
  }
}
