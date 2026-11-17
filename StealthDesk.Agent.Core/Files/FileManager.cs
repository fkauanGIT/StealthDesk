using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.Devices;

namespace StealthDesk.Agent.Core.Files;

public interface IFileManager
{
  /// <summary>A directory's immediate contents. The agent's own system drive when <paramref name="path"/> is empty.</summary>
  DirectoryListing ListDirectory(string path);

  /// <summary>Whether <paramref name="path"/> is a file the agent can read, and its size and name.</summary>
  FileDownloadInfo GetFileInfo(string path);

  /// <summary>The file's bytes, read a chunk at a time rather than all into memory at once.</summary>
  IAsyncEnumerable<byte[]> ReadFile(string path, CancellationToken cancellationToken);
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

  public FileDownloadInfo GetFileInfo(string path)
  {
    if (!File.Exists(path))
    {
      // Also true for a directory: File.Exists is false for one, same as for a path that's simply gone.
      return new FileDownloadInfo(Exists: false, FileName: string.Empty, SizeBytes: 0);
    }

    var file = new FileInfo(path);
    return new FileDownloadInfo(Exists: true, file.Name, file.Length);
  }

  public async IAsyncEnumerable<byte[]> ReadFile(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
  {
    await using var stream = File.OpenRead(path);
    var buffer = new byte[ChunkSizeBytes];
    int read;
    while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
    {
      // A copy: the shared buffer would be overwritten by the next read before an earlier chunk finished sending.
      yield return buffer[..read];
    }
  }

  private const int ChunkSizeBytes = 64 * 1024;

  private static string DefaultRoot() => Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
}
