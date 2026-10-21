using System.Runtime.Versioning;
using StealthDesk.Agent.Windows.Service;

namespace StealthDesk.Agent.Tests;

/// <summary>Where an installed agent lives and what its service is called, for each instance and build. Windows paths.</summary>
[SupportedOSPlatform("windows")]
public class AgentInstallationTests
{
  private const string ProgramFiles = @"C:\Program Files";
  private const string ProgramData = @"C:\ProgramData";

  [Fact]
  public void TheDefaultInstance_IsTheAgent()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths.");

    var installation = Installation(instance: null, debug: false);

    Assert.Equal("StealthDesk.Agent", installation.ServiceName);
    Assert.Equal("StealthDesk Agent", installation.DisplayName);
    Assert.Equal(@"C:\Program Files\StealthDesk\default", installation.InstallFolder);
    Assert.Equal(@"C:\Program Files\StealthDesk\default\StealthDesk.Agent.exe", installation.ExecutablePath);
    Assert.Equal(@"C:\ProgramData\StealthDesk\default", installation.DataFolder);
    Assert.Equal("\"C:\\Program Files\\StealthDesk\\default\\StealthDesk.Agent.exe\" run", installation.ServiceCommandLine);
    Assert.Equal("\"C:\\Program Files\\StealthDesk\\default\\StealthDesk.Agent.exe\" uninstall", installation.UninstallCommandLine);
    Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\StealthDesk.Agent", installation.UninstallKeyPath);
  }

  [Fact]
  public void AnotherInstance_GetsItsOwnServiceFoldersAndCommands()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths.");

    var installation = Installation(instance: "office", debug: false);

    Assert.Equal("StealthDesk.Agent.office", installation.ServiceName);
    Assert.Equal("StealthDesk Agent - office", installation.DisplayName);
    Assert.Equal(@"C:\Program Files\StealthDesk\office", installation.InstallFolder);
    Assert.Equal(@"C:\ProgramData\StealthDesk\office", installation.DataFolder);
    Assert.Equal("\"C:\\Program Files\\StealthDesk\\office\\StealthDesk.Agent.exe\" run --instance office", installation.ServiceCommandLine);
    Assert.Equal("\"C:\\Program Files\\StealthDesk\\office\\StealthDesk.Agent.exe\" uninstall --instance office", installation.UninstallCommandLine);
  }

  [Fact]
  public void ADebugBuild_NeverTouchesAnInstalledAgent()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths.");

    var debug = Installation(instance: null, debug: true);
    var release = Installation(instance: null, debug: false);

    Assert.Equal("StealthDesk.Agent.Debug", debug.ServiceName);
    Assert.Equal(@"C:\Program Files\StealthDesk\Debug\default", debug.InstallFolder);
    Assert.Equal(@"C:\ProgramData\StealthDesk\Debug\default", debug.DataFolder);
    Assert.NotEqual(release.ServiceName, debug.ServiceName);
    Assert.NotEqual(release.UninstallKeyPath, debug.UninstallKeyPath);
  }

  [Theory]
  [InlineData(@"C:\Program Files\StealthDesk\default", true)]
  [InlineData(@"C:\Program Files\StealthDesk\default\", true)]
  [InlineData(@"c:\program files\stealthdesk\DEFAULT", true)]
  [InlineData(@"C:\Users\me\Downloads\agent", false)]
  [InlineData(@"C:\Program Files\StealthDesk\office", false)]
  public void Installers_KnowWhenTheyRunFromTheInstallFolder(string startupFolder, bool fromInstallFolder)
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths.");

    var installer = new AgentInstaller(Installation(instance: null, debug: false), TextWriter.Null);

    Assert.Equal(fromInstallFolder, installer.RunsFromInstallFolder(startupFolder));
  }

  [Fact]
  public void OnlyCopiesInTheTempFolder_AreDeletedAfterUninstalling()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths.");

    var temp = Path.GetTempPath();

    Assert.True(AgentInstaller.IsACopy(Path.Combine(temp, "StealthDesk-agent-0123abcd")));
    Assert.True(AgentInstaller.IsACopy(Path.Combine(temp, "StealthDesk-agent-0123abcd") + Path.DirectorySeparatorChar));
    Assert.False(AgentInstaller.IsACopy(temp));
    Assert.False(AgentInstaller.IsACopy(Path.Combine(temp, "something-else")));
    Assert.False(AgentInstaller.IsACopy(Path.Combine(temp, "nested", "StealthDesk-agent-0123abcd")));
    Assert.False(AgentInstaller.IsACopy(@"C:\Program Files\StealthDesk\default"));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void Uninstalling_RemovesTheEmptyStealthDeskFolders_ButNotWhatHoldsThem(bool debug)
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths.");

    var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"sd-parents-{Guid.NewGuid():N}")).FullName;
    try
    {
      var installation = new AgentInstallation(null, debug, "StealthDesk.Agent.exe", root, root);
      Directory.CreateDirectory(installation.InstallFolder);
      Directory.Delete(installation.InstallFolder);

      AgentInstaller.DeleteEmptyParents(installation.InstallFolder);

      Assert.False(Directory.Exists(Path.Combine(root, "StealthDesk")));
      Assert.True(Directory.Exists(root));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void Uninstalling_KeepsTheStealthDeskFolder_WhileAnotherInstanceIsThere()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths.");

    var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"sd-parents-{Guid.NewGuid():N}")).FullName;
    try
    {
      var installation = new AgentInstallation(null, false, "StealthDesk.Agent.exe", root, root);
      var office = new AgentInstallation("office", false, "StealthDesk.Agent.exe", root, root);
      File.WriteAllText(Path.Combine(Directory.CreateDirectory(office.InstallFolder).FullName, "StealthDesk.Agent.exe"), "");

      AgentInstaller.DeleteEmptyParents(installation.InstallFolder);

      Assert.True(File.Exists(Path.Combine(office.InstallFolder, "StealthDesk.Agent.exe")));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  private static AgentInstallation Installation(string? instance, bool debug) =>
    new(instance, debug, "StealthDesk.Agent.exe", ProgramFiles, ProgramData);
}
