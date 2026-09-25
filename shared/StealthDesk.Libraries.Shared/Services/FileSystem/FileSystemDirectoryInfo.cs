namespace StealthDesk.Libraries.Shared.Services.FileSystem;

public interface IFileSystemDirectory
{
  FileAttributes Attributes { get; }
  DateTime CreationTime { get; }
  bool Exists { get; }
  string FullName { get; }
  DateTime LastWriteTime { get; }
  string Name { get; }
  IFileSystemDirectory? Parent { get; }
}


internal sealed class FileSystemDirectoryInfo(DirectoryInfo directoryInfo) : IFileSystemDirectory
{
  public FileAttributes Attributes => directoryInfo.Attributes;

  public DateTime CreationTime => directoryInfo.CreationTime;

  public bool Exists => directoryInfo.Exists;

  public string FullName => directoryInfo.FullName;

  public DateTime LastWriteTime => directoryInfo.LastWriteTime;

  public string Name => directoryInfo.Name;

  public IFileSystemDirectory? Parent => directoryInfo.Parent is null
    ? null
    : new FileSystemDirectoryInfo(directoryInfo.Parent);
}

