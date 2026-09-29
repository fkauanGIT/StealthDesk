namespace StealthDesk.Branding;

/// <summary>
/// Product names used for folders, services and assemblies.
/// Everything is derived from <see cref="Name"/>, except names that belong to the code itself.
/// </summary>
public static class Brand
{
  public const string Name = "StealthDesk";

  /// <summary><see cref="Name"/> reduced to letters, digits and '_', so it is safe in paths and service names.</summary>
  public static string Key { get; } = string.Concat(Name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));

  /// <summary>Folder under the machine-wide data directory (e.g. <c>C:\ProgramData</c>).</summary>
  public static string DataFolderName => Key;

  public static string AgentName => $"{Key}.Agent";

  /// <summary>Assembly of the web server. Fixed: it follows the project, not the brand.</summary>
  public const string ServerAssemblyName = "StealthDesk.Web.Server";
}
