using MessagePack;

namespace StealthDesk.Contracts.Devices;

/// <summary>One file or folder in a directory the file manager listed.</summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record FileSystemEntry(string Name, bool IsDirectory, long SizeBytes, DateTimeOffset ModifiedAt);

/// <summary>
/// A directory's immediate contents, in the device's own file separator. <paramref name="Exists"/> is false for a
/// path that doesn't exist or isn't a directory, with <paramref name="Entries"/> empty, rather than an error: the
/// viewer may have followed a folder that was since renamed or deleted.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record DirectoryListing(string Path, bool Exists, IReadOnlyList<FileSystemEntry> Entries);
