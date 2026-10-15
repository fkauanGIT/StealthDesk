using StealthDesk.Branding;
using StealthDesk.Contracts.Agents;

namespace StealthDesk.Agent.Core.Settings;

/// <summary>Where an agent instance keeps its settings and logs.</summary>
public interface IAgentPaths
{
  string InstanceName { get; }

  /// <summary>Folder that holds everything of this instance.</summary>
  string InstanceFolder { get; }

  /// <summary>JSON file with the <see cref="AgentSettings"/> the agent writes itself (identity and key).</summary>
  string SettingsFile { get; }

  /// <summary>Base log file name; the logger adds the date to it.</summary>
  string LogFile { get; }
}

/// <summary>
/// <c>{machine data}\StealthDesk\[Debug\]{instance}\</c>. Debug builds get their own folder so development
/// never touches the identity of an installed agent on the same machine.
/// </summary>
public sealed class AgentPaths : IAgentPaths
{
  public const string DefaultInstance = InstanceNames.Default;

  public AgentPaths(string? instanceName, bool isDebugBuild, string? dataRoot = null)
  {
    if (InstanceNames.Validate(instanceName) is { } error)
    {
      throw new ArgumentException(error, nameof(instanceName));
    }

    InstanceName = string.IsNullOrWhiteSpace(instanceName) ? DefaultInstance : instanceName.Trim();

    var root = Path.Combine(
      dataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
      Brand.DataFolderName);

    InstanceFolder = isDebugBuild
      ? Path.Combine(root, "Debug", InstanceName)
      : Path.Combine(root, InstanceName);
  }

  public string InstanceName { get; }
  public string InstanceFolder { get; }
  public string SettingsFile => Path.Combine(InstanceFolder, "agent-settings.json");
  public string LogFile => Path.Combine(InstanceFolder, "logs", "agent.log");
}
