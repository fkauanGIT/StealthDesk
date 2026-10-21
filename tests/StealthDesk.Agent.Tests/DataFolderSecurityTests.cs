using System.Runtime.Versioning;
using System.Security.AccessControl;
using StealthDesk.Agent.Windows.Service;

namespace StealthDesk.Agent.Tests;

/// <summary>The data folder of an installed agent holds the device key: only administrators and SYSTEM may read it.</summary>
public sealed class DataFolderSecurityTests : IDisposable
{
  private readonly string _folder = Path.Combine(Path.GetTempPath(), $"stealthdesk-acl-{Guid.NewGuid():N}");

  public void Dispose()
  {
    if (OperatingSystem.IsWindows() && Directory.Exists(_folder))
    {
      // The owner may always change the permissions back, and then delete what it couldn't read.
      var open = new DirectorySecurity();
      open.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
      new DirectoryInfo(_folder).SetAccessControl(open);
      Directory.Delete(_folder, recursive: true);
    }
  }

  [Fact]
  [SupportedOSPlatform("windows")]
  public void AProtectedFolder_GrantsAdministratorsAndSystemOnly_AndInheritsNothing()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs Windows.");

    DataFolderSecurity.Protect(_folder);

    var (allowed, inheritsFromParent) = DataFolderSecurity.Describe(_folder);
    Assert.False(inheritsFromParent);
    Assert.Equal(
      new[] { DataFolderSecurity.Administrators, DataFolderSecurity.LocalSystem }.Order(),
      allowed.Order());
  }

  [Fact]
  [SupportedOSPlatform("windows")]
  public void FilesWrittenLater_TakeTheFoldersPermissions()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs Windows.");
    Assert.SkipUnless(AgentInstaller.IsElevated(), "Writing into the protected folder needs an administrator.");

    DataFolderSecurity.Protect(_folder);
    var file = Path.Combine(_folder, "logs", "agent.log");
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.WriteAllText(file, "secret");

    var rules = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
      .OfType<FileSystemAccessRule>()
      .Select(x => (System.Security.Principal.SecurityIdentifier)x.IdentityReference)
      .Distinct()
      .Order();
    Assert.Equal(new[] { DataFolderSecurity.Administrators, DataFolderSecurity.LocalSystem }.Order(), rules);
  }

  [Fact]
  [SupportedOSPlatform("windows")]
  public void WithoutAdministratorRights_TheKeyCantBeRead()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs Windows.");
    Assert.SkipWhen(AgentInstaller.IsElevated(), "Run without administrator rights: an administrator may read the folder.");

    DataFolderSecurity.Protect(_folder);

    Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFiles(_folder));
    Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(_folder, "agent-settings.json"), "{}"));
  }
}
