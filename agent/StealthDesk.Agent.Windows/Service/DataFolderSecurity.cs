using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace StealthDesk.Agent.Windows.Service;

/// <summary>
/// The agent's data folder holds the device's private key: only administrators and SYSTEM (the service) may read it.
/// Inherited permissions, such as the Users read access ProgramData grants, are cut off.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DataFolderSecurity
{
  public static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
  public static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

  /// <summary>Creates the folder if needed and leaves full control to administrators and SYSTEM only.</summary>
  public static void Protect(string folder)
  {
    var directory = Directory.CreateDirectory(folder);

    var security = new DirectorySecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    foreach (var identity in new[] { Administrators, LocalSystem })
    {
      security.AddAccessRule(new FileSystemAccessRule(
        identity,
        FileSystemRights.FullControl,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None,
        AccessControlType.Allow));
    }

    directory.SetAccessControl(security);
  }

  /// <summary>Who the folder grants anything to, and whether it still takes permissions from its parent.</summary>
  public static (IReadOnlyList<SecurityIdentifier> Allowed, bool InheritsFromParent) Describe(string folder)
  {
    var security = new DirectoryInfo(folder).GetAccessControl();
    var allowed = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
      .OfType<FileSystemAccessRule>()
      .Where(x => x.AccessControlType == AccessControlType.Allow)
      .Select(x => (SecurityIdentifier)x.IdentityReference)
      .Distinct()
      .ToList();

    return (allowed, !security.AreAccessRulesProtected);
  }
}
