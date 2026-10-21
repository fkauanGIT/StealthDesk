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

  [Fact]
  public void TheServerGiven_WinsOverTheSavedOne_ButTheSavedDeviceIdWinsOverTheOneAskedFor()
  {
    var instance = $"t{Guid.NewGuid():N}"[..20];
    var paths = new AgentPaths(instance, AgentBuild.IsDebug);
    var saved = Guid.NewGuid();
    Directory.CreateDirectory(paths.InstanceFolder);
    File.WriteAllText(paths.SettingsFile, $$"""{ "Agent": { "ServerUrl": "http://old.example.com/", "DeviceId": "{{saved}}" } }""");
    try
    {
      var settings = Settings(new AgentStartup(instance, new Uri("http://new.example.com"), DeviceId: Guid.NewGuid()));

      Assert.Equal(new Uri("http://new.example.com"), settings.ServerUrl);
      Assert.Equal(saved, settings.DeviceId);
    }
    finally
    {
      Directory.Delete(paths.InstanceFolder, recursive: true);
    }
  }

  [Fact]
  public void WithoutAServerGiven_TheSavedOneIsUsed()
  {
    var instance = $"t{Guid.NewGuid():N}"[..20];
    var paths = new AgentPaths(instance, AgentBuild.IsDebug);
    Directory.CreateDirectory(paths.InstanceFolder);
    File.WriteAllText(paths.SettingsFile, """{ "Agent": { "ServerUrl": "http://saved.example.com/" } }""");
    try
    {
      Assert.Equal(new Uri("http://saved.example.com/"), Settings(new AgentStartup(instance)).ServerUrl);
    }
    finally
    {
      Directory.Delete(paths.InstanceFolder, recursive: true);
    }
  }

  private static AgentSettings Settings(AgentStartup startup)
  {
    var builder = Host.CreateApplicationBuilder();
    builder.AddStealthDeskAgent(startup);
    using var services = builder.Services.BuildServiceProvider();
    return services.GetRequiredService<IOptions<AgentSettings>>().Value;
  }
}
