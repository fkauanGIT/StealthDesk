using StealthDesk.Agent.Shared.Options;
using StealthDesk.Libraries.Branding;
using StealthDesk.Libraries.Shared.Services.FileSystem;
using Microsoft.Extensions.Options;

namespace StealthDesk.Agent.Shared.Services;

public interface IFileSystemPathProvider
{
  /// <summary>
  /// Returns the path to the agent's appsettings.json file.
  /// </summary>
  string GetAgentAppSettingsPath();
  /// <summary>
  /// Returns the path to the agent's current log file.
  /// </summary>
  string GetAgentLogFilePath();
  /// <summary>
  /// Returns the directory where agent logs are stored. On Windows this is under CommonApplicationData; on Linux/macOS it is under /var/log/stealthdesk for elevated processes or ~/.stealthdesk/logs for user processes.
  /// </summary>
  string GetAgentLogsDirectoryPath();
  /// <summary>
  /// Returns the instance ID, or the default instance ID when none is configured.
  /// </summary>
  string GetEffectiveInstanceId();
}
public class FileSystemPathProvider(
  ISystemEnvironment systemEnvironment,
  IElevationChecker elevationChecker,
  IFileSystem fileSystem,
  IOptionsMonitor<InstanceOptions> instanceOptions) : IFileSystemPathProvider
{
  private readonly IElevationChecker _elevationChecker = elevationChecker;
  private readonly IFileSystem _fileSystem = fileSystem;
  private readonly IOptionsMonitor<InstanceOptions> _instanceOptions = instanceOptions;
  private readonly ISystemEnvironment _systemEnvironment = systemEnvironment;

  public string GetAgentAppSettingsPath()
  {
    var dir = GetSettingsDirectory();
    return _fileSystem.JoinPaths(GetPathSeparator(), dir, "appsettings.json");
  }

  public string GetAgentLogFilePath()
  {
    return _fileSystem.JoinPaths(GetPathSeparator(), GetAgentLogsDirectoryPath(), "LogFile.log");
  }

  public string GetAgentLogsDirectoryPath()
  {
    if (_systemEnvironment.IsWindows())
    {
      var logsDir = _fileSystem.JoinPaths(GetPathSeparator(),
        _systemEnvironment.GetCommonApplicationDataDirectory(),
        BrandingConstants.WindowsLogDirectoryName);

      logsDir = AppendSubDirectories(logsDir);
      return _fileSystem.JoinPaths(GetPathSeparator(), logsDir, "Logs", BrandingConstants.AgentBaseName);
    }

    if (_systemEnvironment.IsLinux() || _systemEnvironment.IsMacOS())
    {
      var isElevated = _elevationChecker.IsElevated();
      var rootDir = isElevated
        ? $"/var/log/{BrandingConstants.UnixLogDirectoryName}"
        : _fileSystem.JoinPaths(GetPathSeparator(), _systemEnvironment.GetProfileDirectory(), BrandingConstants.UnixHiddenDirectoryName);

      rootDir = AppendSubDirectories(rootDir);
      var logsDir = isElevated ? rootDir : _fileSystem.JoinPaths(GetPathSeparator(), rootDir, "logs");
      return _fileSystem.JoinPaths(GetPathSeparator(), logsDir, BrandingConstants.AgentBaseName);
    }

    throw new PlatformNotSupportedException();
  }

  public string GetEffectiveInstanceId()
  {
    return string.IsNullOrWhiteSpace(_instanceOptions.CurrentValue.InstanceId)
      ? AppConstants.DefaultInstanceId
      : _instanceOptions.CurrentValue.InstanceId;
  }

  private string AppendSubDirectories(string rootDir)
  {
    var instanceId = GetEffectiveInstanceId();

    if (_systemEnvironment.IsWindows())
    {
      if (_systemEnvironment.IsDebug)
      {
        rootDir = _fileSystem.JoinPaths(GetPathSeparator(), rootDir, "Debug");
      }

      rootDir = _fileSystem.JoinPaths(GetPathSeparator(), rootDir, instanceId);

      _ = _fileSystem.CreateDirectory(rootDir).FullName;
      return rootDir;
    }

    // ReSharper disable once InvertIf
    if (_systemEnvironment.IsLinux() || _systemEnvironment.IsMacOS())
    {
      rootDir = _fileSystem.JoinPaths(GetPathSeparator(), rootDir, instanceId);

      _ = _fileSystem.CreateDirectory(rootDir).FullName;
      return rootDir;
    }

    throw new PlatformNotSupportedException();
  }

  private char GetPathSeparator()
  {
    return _systemEnvironment.IsWindows() ? '\\' : '/';
  }

  private string GetSettingsDirectory()
  {
    if (_systemEnvironment.IsWindows())
    {
      var rootDir = _fileSystem.JoinPaths(
        GetPathSeparator(),
        _systemEnvironment.GetCommonApplicationDataDirectory(),
        BrandingConstants.WindowsInstallDirectoryName);

      return AppendSubDirectories(rootDir);
    }

    // ReSharper disable once InvertIf
    if (_systemEnvironment.IsLinux() || _systemEnvironment.IsMacOS())
    {
      var rootDir = _elevationChecker.IsElevated()
        ? $"/etc/{BrandingConstants.UnixConfigDirectoryName}"
        : _fileSystem.JoinPaths(GetPathSeparator(), _systemEnvironment.GetProfileDirectory(), BrandingConstants.UnixHiddenDirectoryName);

      return AppendSubDirectories(rootDir);
    }

    throw new PlatformNotSupportedException();
  }
}
