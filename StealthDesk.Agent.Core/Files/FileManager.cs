using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.Devices;

namespace StealthDesk.Agent.Core.Files;

public interface IFileManager
{
  /// <summary>A directory's immediate contents. The agent's own system drive when <paramref name="path"/> is empty.</summary>
  DirectoryListing ListDirectory(string path);
}

/// <summary>
/// Reads the agent's own machine's filesystem directly, with whatever access the account running it already has:
/// no sandboxing of the path, the same as the terminal running whatever command it's given. The permission to use
/// the feature at all is the only gate.
/// </summary>
public sealed class FileManager(ILogger<FileManager> logger) : IFileManager
{
  public DirectoryListing ListDirectory(string path)
  {
    var target = string.IsNullOrWhiteSpace(path) ? DefaultRoot() : path;
    if (!Directory.Exists(target))
    {
      return new DirectoryListing(target, Exists: false, []);
    }

    try
    {
      var entries = new DirectoryInfo(target).EnumerateFileSystemInfos()
        .Select(entry => new FileSystemEntry(
          entry.Name,
          entry is DirectoryInfo,
          entry is FileInfo file ? file.Length : 0,
          entry.LastWriteTimeUtc))
        .OrderByDescending(entry => entry.IsDirectory)
        .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
      return new DirectoryListing(target, Exists: true, entries);
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
    {
      // Listed as missing rather than erroring the whole call: the viewer can't tell "denied" from "gone" anyway.
      logger.LogDebug(ex, "Couldn't list {Path}.", target);
      return new DirectoryListing(target, Exists: false, []);
    }
  }

  private static string DefaultRoot() => Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
}
