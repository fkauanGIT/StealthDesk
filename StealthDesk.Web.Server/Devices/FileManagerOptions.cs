namespace StealthDesk.Web.Server.Devices;

/// <summary>Rules the server applies to the file manager. Bound from the <c>FileManager</c> section.</summary>
public sealed class FileManagerOptions
{
  public const string Section = "FileManager";

  /// <summary>The largest file a download may carry. A bigger one refuses before any of it is sent.</summary>
  public long MaxDownloadSizeBytes { get; init; } = 2L * 1024 * 1024 * 1024;
}
