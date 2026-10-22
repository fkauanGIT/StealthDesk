using StealthDesk.Contracts.Agents;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The command the Add device page copies: it downloads from the page's server and installs only what matches.</summary>
public class AgentInstallCommandTests
{
  private static readonly AgentDownload Download = new("/downloads/agent/windows", "StealthDesk.Agent.exe", 53_000_000, "ABCDEF0123456789");

  [Fact]
  public void ItDownloadsFromTheServerThePageCameFrom()
  {
    var command = AgentInstallCommand.Windows(new Uri("https://desk.example.com/devices/add?x=1"), Download, "id:secret");

    Assert.Contains("-Uri 'https://desk.example.com/downloads/agent/windows' -OutFile $agent", command, StringComparison.Ordinal);
    Assert.Contains("--server 'https://desk.example.com'", command, StringComparison.Ordinal);
  }

  [Fact]
  public void ItInstallsOnlyWhenTheHashMatches_AndOtherwiseDeletesTheFile()
  {
    var command = AgentInstallCommand.Windows(new Uri("https://desk.example.com"), Download, "id:secret");

    var check = command.IndexOf("if ((Get-FileHash -Algorithm SHA256 $agent).Hash -eq 'ABCDEF0123456789') {", StringComparison.Ordinal);
    var install = command.IndexOf("& $agent install", StringComparison.Ordinal);
    var otherwise = command.IndexOf("} else { Remove-Item $agent; Write-Error", StringComparison.Ordinal);
    Assert.True(check >= 0 && install > check && otherwise > install, command);
    Assert.Single(System.Text.RegularExpressions.Regex.Matches(command, @"& \$agent"));
  }

  [Fact]
  public void ItCarriesTheInstallerKey_AndNothingElseSecret()
  {
    var command = AgentInstallCommand.Windows(new Uri("https://desk.example.com"), Download, "0f8fad5b-d9cb-469f-a165-70867728950e:the-secret");

    Assert.Contains("--installer-key '0f8fad5b-d9cb-469f-a165-70867728950e:the-secret'", command, StringComparison.Ordinal);
    Assert.DoesNotContain("password", command, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("token", command, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void ADeviceIdAndAnInstance_AreAddedWhenGiven()
  {
    var id = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

    var command = AgentInstallCommand.Windows(new Uri("https://desk.example.com"), Download, "id:secret", id, "office");

    Assert.Contains("--installer-key 'id:secret' --device-id 3f2504e0-4f89-11d3-9a0c-0305e82c3301 --instance 'office'; Remove-Item $agent", command, StringComparison.Ordinal);
  }

  [Fact]
  public void AQuoteInAValue_CantEndItsString()
  {
    var command = AgentInstallCommand.Windows(new Uri("https://desk.example.com"), Download, "id:it's");

    Assert.Contains("--installer-key 'id:it''s'", command, StringComparison.Ordinal);
  }
}
