using System.Text.Json.Nodes;
using StealthDesk.Agent.Core.Settings;
using StealthDesk.Agent.Tests.Fakes;

namespace StealthDesk.Agent.Tests;

public sealed class SettingsTests : IDisposable
{
  private readonly TempAgentFolder _folder = new();

  public void Dispose() => _folder.Dispose();

  [Fact]
  public async Task SavedKey_IsThereAfterARestart()
  {
    await _folder.OpenStore().SavePrivateKeyAsync("private-key", TestContext.Current.CancellationToken);

    Assert.Equal("private-key", _folder.OpenStore().Current.PrivateKey);
  }

  [Fact]
  public async Task SavingTheIdentity_KeepsTheKey()
  {
    var store = _folder.OpenStore();
    var deviceId = Guid.NewGuid();

    await store.SavePrivateKeyAsync("private-key", TestContext.Current.CancellationToken);
    await store.SaveIdentityAsync(deviceId, Guid.Empty, TestContext.Current.CancellationToken);

    var reopened = _folder.OpenStore().Current;
    Assert.Equal("private-key", reopened.PrivateKey);
    Assert.Equal(deviceId, reopened.DeviceId);
  }

  [Fact]
  public async Task Saving_KeepsOtherSectionsOfTheFile()
  {
    Directory.CreateDirectory(_folder.Paths.InstanceFolder);
    await File.WriteAllTextAsync(_folder.Paths.SettingsFile, """{ "Notes": { "Owner": "IT" } }""", TestContext.Current.CancellationToken);

    await _folder.OpenStore().SavePrivateKeyAsync("private-key", TestContext.Current.CancellationToken);

    var document = JsonNode.Parse(await File.ReadAllTextAsync(_folder.Paths.SettingsFile, TestContext.Current.CancellationToken))!;
    Assert.Equal("IT", document["Notes"]!["Owner"]!.GetValue<string>());
    Assert.False(File.Exists(_folder.Paths.SettingsFile + ".tmp"));
  }

  [Theory]
  [InlineData(false, "StealthDesk", "default")]
  [InlineData(true, "StealthDesk", "Debug", "default")]
  public void Paths_SeparateDebugBuildsFromInstalledAgents(bool debug, params string[] expected)
  {
    var paths = new AgentPaths(null, debug, dataRoot: _folder.Root);

    Assert.Equal(Path.Combine([_folder.Root, .. expected]), paths.InstanceFolder);
    Assert.StartsWith(paths.InstanceFolder, paths.SettingsFile);
    Assert.StartsWith(paths.InstanceFolder, paths.LogFile);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("lab")]
  [InlineData("office-2")]
  [InlineData("test_server")]
  public void InstanceNames_AcceptsSimpleNames(string? name)
  {
    Assert.Null(InstanceNames.Validate(name));
  }

  [Theory]
  [InlineData("default")]
  [InlineData("DEFAULT")]
  [InlineData("..")]
  [InlineData("a/b")]
  [InlineData(@"a\b")]
  [InlineData("-lab")]
  [InlineData("name with spaces")]
  public void InstanceNames_RejectsNamesThatBreakFoldersOrClash(string name)
  {
    Assert.NotNull(InstanceNames.Validate(name));
    Assert.Throws<ArgumentException>(() => new AgentPaths(name, isDebugBuild: false, dataRoot: _folder.Root));
  }
}
