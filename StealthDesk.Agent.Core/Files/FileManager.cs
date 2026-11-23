using System.IO.Compression;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.Devices;

namespace StealthDesk.Agent.Core.Files;

/// <summary>
/// What <see cref="IFileManager.PrepareDownload"/> resolved a path to send: a plain file as-is, or a folder zipped
/// first into a temporary file that <paramref name="SourcePath"/> names. <paramref name="IsTemporary"/> tells the
/// caller to delete <paramref name="SourcePath"/> once the bytes are sent; a plain file is never deleted.
/// </summary>
public sealed record PreparedDownload(bool Exists, string FileName, long SizeBytes, string SourcePath, bool IsTemporary)
{
  public static readonly PreparedDownload NotFound = new(false, string.Empty, 0, string.Empty, false);
}

/// <summary>
/// What <see cref="IFileManager.PrepareUpload"/> resolved an upload to: a path to write to, or a reason it can't
/// proceed (the destination folder doesn't exist, or the file is already there and overwriting wasn't allowed).
/// </summary>
public sealed record PreparedUpload(bool Accepted, string TargetPath, string? Error)
{
  public static PreparedUpload Refuse(string error) => new(false, string.Empty, error);

  public static PreparedUpload Accept(string targetPath) => new(true, targetPath, null);
}

/// <summary>What <see cref="IFileManager.CreateDirectory"/> did: the new directory's full path, or why it refused.</summary>
public sealed record CreateDirectoryResult(bool Accepted, string TargetPath, string? Error)
{
  public static CreateDirectoryResult Refuse(string error) => new(false, string.Empty, error);

  public static CreateDirectoryResult Accept(string targetPath) => new(true, targetPath, null);
}

public interface IFileManager
{
  /// <summary>A directory's immediate contents. The agent's own system drive when <paramref name="path"/> is empty.</summary>
  DirectoryListing ListDirectory(string path);

  /// <summary>
  /// Resolves <paramref name="path"/> for download: a file as-is, or a folder zipped into a temporary file first
  /// (built right away, so its real size is known before any byte is sent). Not found, or a folder that couldn't
  /// be zipped, answers <see cref="PreparedDownload.NotFound"/>.
  /// </summary>
  PreparedDownload PrepareDownload(string path);

  /// <summary>The file's bytes, read a chunk at a time rather than all into memory at once.</summary>
  IAsyncEnumerable<byte[]> ReadFile(string path, CancellationToken cancellationToken);

  /// <summary>
  /// Resolves where an upload named <paramref name="fileName"/> should land inside <paramref name="directoryPath"/>.
  /// Doesn't touch the disk: a plain existence check, same as every other guard in this class.
  /// </summary>
  PreparedUpload PrepareUpload(string directoryPath, string fileName, bool overwrite);

  /// <summary>Writes <paramref name="chunks"/> to <paramref name="path"/>, replacing whatever was there.</summary>
  Task WriteFile(string path, IAsyncEnumerable<byte[]> chunks, CancellationToken cancellationToken);

  /// <summary>Creates an empty directory named <paramref name="name"/> inside <paramref name="directoryPath"/>.</summary>
  CreateDirectoryResult CreateDirectory(string directoryPath, string name);
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

  public PreparedDownload PrepareDownload(string path)
  {
    if (Directory.Exists(path))
    {
      return ZipDirectory(path);
    }

    if (!File.Exists(path))
    {
      return PreparedDownload.NotFound;
    }

    var file = new FileInfo(path);
    return new PreparedDownload(true, file.Name, file.Length, path, IsTemporary: false);
  }

  private PreparedDownload ZipDirectory(string directoryPath)
  {
    var zipPath = Path.Combine(Path.GetTempPath(), $"stealthdesk-download-{Guid.NewGuid()}.zip");
    try
    {
      using (var zipStream = File.Create(zipPath))
      using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
      {
        AddDirectoryToZip(archive, directoryPath, entryPrefix: string.Empty);
      }

      var zipName = $"{new DirectoryInfo(directoryPath).Name}.zip";
      var zipSize = new FileInfo(zipPath).Length;
      return new PreparedDownload(true, zipName, zipSize, zipPath, IsTemporary: true);
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
    {
      logger.LogDebug(ex, "Couldn't zip {Path}.", directoryPath);
      TryDeleteTempZip(zipPath);
      return PreparedDownload.NotFound;
    }
  }

  private static void AddDirectoryToZip(ZipArchive archive, string directoryPath, string entryPrefix)
  {
    foreach (var filePath in Directory.EnumerateFiles(directoryPath))
    {
      var entryName = $"{entryPrefix}{Path.GetFileName(filePath)}";
      archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Fastest);
    }

    foreach (var subdirectoryPath in Directory.EnumerateDirectories(directoryPath))
    {
      AddDirectoryToZip(archive, subdirectoryPath, $"{entryPrefix}{Path.GetFileName(subdirectoryPath)}/");
    }
  }

  private void TryDeleteTempZip(string zipPath)
  {
    try
    {
      File.Delete(zipPath);
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
    {
      logger.LogDebug(ex, "Couldn't delete the temporary zip {Path}.", zipPath);
    }
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

  public PreparedUpload PrepareUpload(string directoryPath, string fileName, bool overwrite)
  {
    if (!Directory.Exists(directoryPath))
    {
      return PreparedUpload.Refuse("The destination folder doesn't exist.");
    }

    // The permission to write anywhere the agent's own account can is the only gate, same as reading: a fileName
    // that climbs out of directoryPath lands wherever that account could already reach some other way.
    var targetPath = Path.Combine(directoryPath, fileName);
    if (File.Exists(targetPath) && !overwrite)
    {
      return PreparedUpload.Refuse("A file with that name already exists.");
    }

    return PreparedUpload.Accept(targetPath);
  }

  public async Task WriteFile(string path, IAsyncEnumerable<byte[]> chunks, CancellationToken cancellationToken)
  {
    await using var stream = File.Create(path);
    await foreach (var chunk in chunks.WithCancellation(cancellationToken))
    {
      await stream.WriteAsync(chunk, cancellationToken);
    }
  }

  public CreateDirectoryResult CreateDirectory(string directoryPath, string name)
  {
    if (!Directory.Exists(directoryPath))
    {
      return CreateDirectoryResult.Refuse("The destination folder doesn't exist.");
    }

    var targetPath = Path.Combine(directoryPath, name);
    if (Directory.Exists(targetPath) || File.Exists(targetPath))
    {
      return CreateDirectoryResult.Refuse("Something with that name already exists.");
    }

    try
    {
      Directory.CreateDirectory(targetPath);
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
    {
      logger.LogDebug(ex, "Couldn't create directory {Path}.", targetPath);
      return CreateDirectoryResult.Refuse("The device couldn't create the folder.");
    }

    return CreateDirectoryResult.Accept(targetPath);
  }

  private const int ChunkSizeBytes = 64 * 1024;

  private static string DefaultRoot() => Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
}
