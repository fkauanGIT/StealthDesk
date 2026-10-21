using StealthDesk.Agent.Core.Settings;
using StealthDesk.Branding;

namespace StealthDesk.Agent.Windows.Service;

/// <summary>
/// Names and places of an agent instance installed as a Windows service. Debug builds get their own service and folders,
/// like their own data folder, so development never touches an installed agent.
/// </summary>
public sealed class AgentInstallation
{
  public AgentInstallation(string? instanceName, bool isDebugBuild, string executableName, string? programFiles = null, string? dataRoot = null)
  {
    Paths = new AgentPaths(instanceName, isDebugBuild, dataRoot);
    IsDebugBuild = isDebugBuild;
    ExecutableName = executableName;

    var root = Path.Combine(programFiles ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Brand.DataFolderName);
    InstallFolder = isDebugBuild ? Path.Combine(root, "Debug", InstanceName) : Path.Combine(root, InstanceName);
  }

  public AgentPaths Paths { get; }

  public string InstanceName => Paths.InstanceName;

  public bool IsDefaultInstance => InstanceName == AgentPaths.DefaultInstance;

  public bool IsDebugBuild { get; }

  public string ExecutableName { get; }

  /// <summary>Where the agent's files are copied to and the service runs from.</summary>
  public string InstallFolder { get; }

  public string ExecutablePath => Path.Combine(InstallFolder, ExecutableName);

  /// <summary>Settings, the device key and logs: readable only by administrators and SYSTEM once installed.</summary>
  public string DataFolder => Paths.InstanceFolder;

  public string ServiceName => Brand.AgentName + (IsDebugBuild ? ".Debug" : "") + (IsDefaultInstance ? "" : $".{InstanceName}");

  public string DisplayName => $"{Brand.Name} Agent" + (IsDebugBuild ? " (Debug)" : "") + (IsDefaultInstance ? "" : $" - {InstanceName}");

  /// <summary>The entry in Settings > Apps (Add or remove programs), under HKEY_LOCAL_MACHINE.</summary>
  public string UninstallKeyPath => $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ServiceName}";

  /// <summary>What the service runs: the installed executable, never the one install was started from.</summary>
  public string ServiceCommandLine => CommandLine("run");

  public string UninstallCommandLine => CommandLine("uninstall");

  private string CommandLine(string command) =>
    $"\"{ExecutablePath}\" {command}" + (IsDefaultInstance ? "" : $" --instance {InstanceName}");
}
