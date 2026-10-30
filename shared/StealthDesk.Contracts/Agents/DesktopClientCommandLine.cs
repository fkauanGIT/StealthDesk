namespace StealthDesk.Contracts.Agents;

/// <summary>What a desktop client is started with: the agent instance it belongs to and that instance's data folder.</summary>
/// <param name="Instance">The agent's instance, which names what the client shares with it.</param>
/// <param name="DataFolder">The instance's data folder, where the client writes its log.</param>
public sealed record DesktopClientStart(string Instance, string DataFolder);

/// <summary>How the agent starts a desktop client, and how the client reads that back.</summary>
public static class DesktopClientCommandLine
{
  public const string InstanceOption = "--instance";
  public const string DataFolderOption = "--data-folder";

  /// <summary>The arguments for <paramref name="start"/>, quoted for a Windows command line.</summary>
  public static string Arguments(DesktopClientStart start) =>
    $"{InstanceOption} {Quote(start.Instance)} {DataFolderOption} {Quote(Path.TrimEndingDirectorySeparator(start.DataFolder))}";

  /// <summary>What the client was started with, or null when either value is missing.</summary>
  public static DesktopClientStart? Parse(IReadOnlyList<string> args)
  {
    string? instance = null;
    string? dataFolder = null;
    for (var i = 0; i + 1 < args.Count; i++)
    {
      if (args[i] == InstanceOption)
      {
        instance = args[++i];
      }
      else if (args[i] == DataFolderOption)
      {
        dataFolder = args[++i];
      }
    }

    return string.IsNullOrWhiteSpace(instance) || string.IsNullOrWhiteSpace(dataFolder) ? null : new DesktopClientStart(instance, dataFolder);
  }

  // Instance names have no quotes, and a folder can't hold one; without a trailing backslash, nothing escapes the end.
  private static string Quote(string value) => $"\"{value}\"";
}
