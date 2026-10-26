using System.Management.Automation;

namespace StealthDesk.Agent.Core.Terminal;

/// <summary>
/// PowerShell's built-in modules (CimCmdlets, Diagnostics, Management, Security, Utility...), carried inside the
/// agent. PowerShell loads them from a Modules folder next to itself, which an agent made of one executable doesn't
/// have: without them Get-CimInstance fails and objects lose properties their modules add, like Get-Acl's Owner. Their
/// code is in the agent already; these are the manifests and formats that describe it, written to a folder of the
/// agent's own and imported from there when a session starts.
/// </summary>
internal static class PowerShellModules
{
  private const string ResourcePrefix = "PowerShellModules/";
  private static readonly Lock Gate = new();
  private static readonly HashSet<string> Written = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// Writes the modules under <paramref name="instanceFolder"/>, once per run of the agent. The folder must be
  /// writable only by administrators: a session imports what is there, running as the agent's account.
  /// </summary>
  /// <returns>The folder holding the modules.</returns>
  public static string Prepare(string instanceFolder)
  {
    var folder = Path.Combine(instanceFolder, "powershell", PSVersionInfo.PSVersion.ToString(), "Modules");
    lock (Gate)
    {
      if (Written.Add(folder) || !Directory.Exists(folder))
      {
        Extract($"{ResourcePrefix}{(OperatingSystem.IsWindows() ? "win" : "unix")}/", folder);
      }
    }

    return folder;
  }

  // Written again on every run of the agent: a few hundred kilobytes, and a copy changed since is put right.
  private static void Extract(string prefix, string folder)
  {
    var assembly = typeof(PowerShellModules).Assembly;
    var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
    foreach (var name in assembly.GetManifestResourceNames().Where(x => x.StartsWith(prefix, StringComparison.Ordinal)))
    {
      // Built on Windows the names keep its separators.
      var target = Path.GetFullPath(Path.Combine(folder, name[prefix.Length..].Replace('\\', '/')));
      if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      using var resource = assembly.GetManifestResourceStream(name)!;
      using var file = File.Create(target);
      resource.CopyTo(file);
    }
  }
}
