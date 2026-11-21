namespace StealthDesk.Web.Server.Devices;

/// <summary>Rules the server applies to the file manager. Bound from the <c>FileManager</c> section.</summary>
public sealed class FileManagerOptions
{
  public const string Section = "FileManager";

  /// <summary>The largest file a download may carry. A bigger one refuses before any of it is sent.</summary>
  public long MaxDownloadSizeBytes { get; init; } = 2L * 1024 * 1024 * 1024;

  /// <summary>
  /// The largest file an upload may carry. Unlike <see cref="MaxDownloadSizeBytes"/>, the browser's declared
  /// length is the only thing to check it against: the size isn't known for certain until the last byte arrives.
  /// </summary>
  public long MaxUploadSizeBytes { get; init; } = 2L * 1024 * 1024 * 1024;
}
