using StealthDesk.Branding;

namespace StealthDesk.Agent.Windows.Service;

/// <summary>
/// The files an install copies. A regular build is a folder of files next to the executable; a single-file build is
/// the executable and the desktop client's folder next to it, maybe in a folder full of other things that aren't ours.
/// </summary>
public static class AgentFiles
{
  /// <summary>Paths, relative to the executable's folder, of what makes up the agent at <paramref name="executable"/>.</summary>
  public static IReadOnlyList<string> Of(string executable)
  {
    var folder = Path.GetDirectoryName(Path.GetFullPath(executable))!;
    var mainAssembly = Path.Combine(folder, Path.GetFileNameWithoutExtension(executable) + ".dll");

    if (File.Exists(mainAssembly))
    {
      return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(folder, x)).ToList();
    }

    var desktopClient = Path.Combine(folder, Brand.DesktopClientFolderName);
    var desktopClientFiles = Directory.Exists(desktopClient)
      ? Directory.EnumerateFiles(desktopClient, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(folder, x))
      : [];
    return [Path.GetFileName(executable), .. desktopClientFiles];
  }

  /// <summary>Copies what makes up the agent at <paramref name="executable"/> into <paramref name="destination"/>.</summary>
  public static void Copy(string executable, string destination)
  {
    var folder = Path.GetDirectoryName(Path.GetFullPath(executable))!;
    foreach (var file in Of(executable))
    {
      var target = Path.Combine(destination, file);
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.Copy(Path.Combine(folder, file), target, overwrite: true);
    }
  }
}
