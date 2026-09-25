using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StealthDesk.Agent.Shared.Options;
using StealthDesk.Agent.Shared.Services;
using StealthDesk.Libraries.Shared.Services.FileSystem;

namespace StealthDesk.Agent.Shared.Tests;

public sealed class OptionsAccessorTests : IDisposable
{
  private readonly string _settingsDirectory =
    Path.Combine(Path.GetTempPath(), $"stealthdesk-options-{Guid.NewGuid():N}");

  private string SettingsPath => Path.Combine(_settingsDirectory, "appsettings.json");

  public OptionsAccessorTests()
  {
    Directory.CreateDirectory(_settingsDirectory);
  }

  public void Dispose()
  {
    Directory.Delete(_settingsDirectory, recursive: true);
  }

  [Fact]
  public async Task UpdatePrivateKey_PersistsAndIsReadByNewAccessor()
  {
    var firstRun = CreateAccessor();
    Assert.Null(firstRun.PrivateKey);

    await firstRun.UpdatePrivateKey("private-key-base64");

    // A new accessor over a fresh configuration simulates the agent restarting.
    var secondRun = CreateAccessor();
    Assert.Equal("private-key-base64", secondRun.PrivateKey);
  }

  [Fact]
  public async Task UpdateId_KeepsThePrivateKey()
  {
    var deviceId = Guid.NewGuid();
    var firstRun = CreateAccessor();

    await firstRun.UpdatePrivateKey("private-key-base64");
    await firstRun.UpdateId(deviceId);

    var secondRun = CreateAccessor();
    Assert.Equal(deviceId, secondRun.DeviceId);
    Assert.Equal("private-key-base64", secondRun.PrivateKey);
  }

  private OptionsAccessor CreateAccessor()
  {
    var configuration = new ConfigurationBuilder()
      .AddJsonFile(SettingsPath, optional: true, reloadOnChange: false)
      .Build();

    var services = new ServiceCollection();
    services.Configure<AgentAppOptions>(configuration.GetSection(AgentAppOptions.SectionKey));
    services.Configure<InstanceOptions>(configuration.GetSection(InstanceOptions.SectionKey));
    var provider = services.BuildServiceProvider();

    var pathProvider = new Mock<IFileSystemPathProvider>();
    pathProvider.Setup(x => x.GetAgentAppSettingsPath()).Returns(SettingsPath);

    return new OptionsAccessor(
      new FileSystem(NullLogger<FileSystem>.Instance),
      pathProvider.Object,
      provider.GetRequiredService<IOptionsMonitor<AgentAppOptions>>(),
      provider.GetRequiredService<IOptions<InstanceOptions>>());
  }
}
