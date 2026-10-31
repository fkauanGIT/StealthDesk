using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using StealthDesk.Branding;

namespace StealthDesk.Agent.Windows.Service;

/// <summary>
/// Installs the agent as a Windows service and removes it again. Install copies the agent to its install folder,
/// joins the server from there with the installer key (so the service itself never holds the key), creates the
/// service and its Apps entry and starts it. Uninstall removes all of that, the device's identity included.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentInstaller(AgentInstallation installation, TextWriter output)
{
  private const int Failed = 1;
  private static readonly string CopyPrefix = $"{Brand.DataFolderName}-agent-";
  private static readonly TimeSpan DeleteRetry = TimeSpan.FromSeconds(2);

  /// <summary>
  /// Joins the server with the given settings folder and returns null once the server accepted the device, or why it
  /// didn't. Runs the agent in this process until then.
  /// </summary>
  public delegate Task<string?> Join(CancellationToken cancellationToken);

  public static bool IsElevated()
  {
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
  }

  /// <summary>Whether this process runs from the install folder, which install and uninstall replace or delete.</summary>
  public bool RunsFromInstallFolder(string startupFolder) =>
    string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(startupFolder)), Path.TrimEndingDirectorySeparator(installation.InstallFolder), StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// Copies the agent out of its install folder and runs the same command from the copy, which then may replace or
  /// delete the folder. The copy deletes itself after uninstalling.
  /// </summary>
  public int ContinueFromACopy(string executable, IEnumerable<string> arguments)
  {
    var copy = Path.Combine(Path.GetTempPath(), $"{CopyPrefix}{Guid.NewGuid():N}");
    AgentFiles.Copy(executable, copy);

    var start = new ProcessStartInfo(Path.Combine(copy, installation.ExecutableName)) { UseShellExecute = false };
    foreach (var argument in arguments)
    {
      start.ArgumentList.Add(argument);
    }

    Process.Start(start);
    output.WriteLine($"Continuing from a copy in {copy}, so the install folder can be replaced.");
    return 0;
  }

  /// <param name="executable">The agent being installed: it, or its folder's files for a regular build, are copied.</param>
  public async Task<int> InstallAsync(string executable, Join join, CancellationToken cancellationToken)
  {
    if (!IsElevated())
    {
      output.WriteLine("Install needs an administrator: run it from an elevated terminal.");
      return Failed;
    }

    var existingData = Directory.Exists(installation.DataFolder);
    var existingService = ServiceManager.Exists(installation.ServiceName);
    try
    {
      output.WriteLine($"Installing {installation.DisplayName} in {installation.InstallFolder}.");

      // A running agent would use the same identity: stop it while joining, but keep it until the server accepts.
      ServiceManager.Stop(installation.ServiceName);
      StopProcessesFromInstallFolder();

      // Before the key pair exists, so it is never written where others can read it.
      DataFolderSecurity.Protect(installation.DataFolder);

      output.WriteLine("Joining the server.");
      if (await join(cancellationToken) is { } refusal)
      {
        output.WriteLine($"The server didn't accept this device: {refusal}");
        KeepWhatWasThere(existingService, existingData);
        return Failed;
      }

      ServiceManager.Delete(installation.ServiceName);
      DeleteFolder(installation.InstallFolder);
      AgentFiles.Copy(executable, installation.InstallFolder);
      ServiceManager.Create(installation, $"Keeps this device connected to its {Brand.Name} server.");
      WriteAppsEntry();
      EnableSecureAttention();
      ServiceManager.Start(installation.ServiceName);
      output.WriteLine($"Installed: the {installation.ServiceName} service is running.");
      return 0;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      output.WriteLine($"Install failed: {ex.Message}");
      Undo(existingData);
      return Failed;
    }
  }

  public async Task<int> UninstallAsync(string startupFolder)
  {
    if (!IsElevated())
    {
      output.WriteLine("Uninstall needs an administrator: run it from an elevated terminal.");
      return Failed;
    }

    output.WriteLine($"Uninstalling {installation.DisplayName}.");
    RemoveService();
    StopProcessesFromInstallFolder();
    await DeleteFolderWithRetriesAsync(installation.InstallFolder);
    await DeleteFolderWithRetriesAsync(installation.DataFolder);
    DeleteEmptyParents(installation.InstallFolder);
    DeleteEmptyParents(installation.DataFolder);
    DeleteAppsEntry();
    DisableSecureAttention();
    output.WriteLine("Uninstalled: the service, its files and the device's identity are gone.");

    if (IsACopy(startupFolder))
    {
      DeleteAfterExit(startupFolder);
    }

    return 0;
  }

  private void RemoveService()
  {
    ServiceManager.Stop(installation.ServiceName);
    ServiceManager.Delete(installation.ServiceName);
  }

  // A refused join changed nothing yet: an earlier installation runs again as it was, and a new one leaves nothing.
  private void KeepWhatWasThere(bool existingService, bool existingData)
  {
    if (!existingService)
    {
      Undo(existingData);
      return;
    }

    try
    {
      ServiceManager.Start(installation.ServiceName);
      output.WriteLine("The installed agent keeps running as it was.");
    }
    catch (Exception ex)
    {
      output.WriteLine($"Couldn't start the installed agent again: {ex.Message}");
    }
  }

  private void Undo(bool keepData)
  {
    try
    {
      RemoveService();
      DeleteAppsEntry();
      DisableSecureAttention();
      DeleteFolder(installation.InstallFolder);
      if (!keepData)
      {
        DeleteFolder(installation.DataFolder);
      }
    }
    catch (Exception ex)
    {
      output.WriteLine($"Couldn't undo everything: {ex.Message}");
    }
  }

  // The agent and its desktop clients, which would hold their files.
  private void StopProcessesFromInstallFolder()
  {
    StopProcesses(installation.ExecutablePath);
    StopProcesses(Path.Combine(installation.InstallFolder, Brand.DesktopClientFolderName, $"{Brand.DesktopClientName}.exe"));
  }

  private static void StopProcesses(string executable)
  {
    foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
    {
      using (process)
      {
        try
        {
          if (process.Id != Environment.ProcessId &&
            string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
          {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(15));
          }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
          // Gone already, or not ours to inspect.
        }
      }
    }
  }

  private void WriteAppsEntry()
  {
    using var key = Registry.LocalMachine.CreateSubKey(installation.UninstallKeyPath, writable: true);
    var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
    key.SetValue("DisplayName", installation.DisplayName);
    key.SetValue("DisplayVersion", version);
    key.SetValue("Publisher", Brand.Name);
    key.SetValue("DisplayIcon", installation.ExecutablePath);
    key.SetValue("InstallLocation", installation.InstallFolder);
    key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
    key.SetValue("UninstallString", installation.UninstallCommandLine);
    key.SetValue("QuietUninstallString", installation.UninstallCommandLine);
    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
  }

  private void DeleteAppsEntry() => Registry.LocalMachine.DeleteSubKeyTree(installation.UninstallKeyPath, throwOnMissingSubKey: false);

  // Ctrl+Alt+Del from a viewer needs this; a failure here isn't worth failing the install over.
  private void EnableSecureAttention()
  {
    try
    {
      SecureAttention.Enable();
    }
    catch (Exception ex)
    {
      output.WriteLine($"Couldn't allow Ctrl+Alt+Del (SoftwareSASGeneration): {ex.Message}");
    }
  }

  private void DisableSecureAttention()
  {
    try
    {
      SecureAttention.Disable();
    }
    catch (Exception ex)
    {
      output.WriteLine($"Couldn't undo the Ctrl+Alt+Del policy: {ex.Message}");
    }
  }

  private static void DeleteFolder(string folder)
  {
    if (Directory.Exists(folder))
    {
      Directory.Delete(folder, recursive: true);
    }
  }

  /// <summary>
  /// Removes the brand's folders above an instance's (<c>StealthDesk\Debug</c>, <c>StealthDesk</c>) once nothing is
  /// left in them, so another instance's files are never touched.
  /// </summary>
  public static void DeleteEmptyParents(string instanceFolder)
  {
    for (var folder = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(instanceFolder));
      folder is not null && IsBrandFolder(folder) && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any();
      folder = Path.GetDirectoryName(folder))
    {
      Directory.Delete(folder);
    }
  }

  // Only StealthDesk itself or a folder inside it, never Program Files or ProgramData.
  private static bool IsBrandFolder(string folder) =>
    string.Equals(Path.GetFileName(folder), Brand.DataFolderName, StringComparison.OrdinalIgnoreCase) ||
    string.Equals(Path.GetFileName(Path.GetDirectoryName(folder)), Brand.DataFolderName, StringComparison.OrdinalIgnoreCase);

  // A process that just exited can hold its files for a moment.
  private async Task DeleteFolderWithRetriesAsync(string folder)
  {
    for (var attempt = 1; ; attempt++)
    {
      try
      {
        DeleteFolder(folder);
        return;
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
      {
        output.WriteLine($"{folder} is still in use; trying again.");
        await Task.Delay(DeleteRetry);
      }
    }
  }

  /// <summary>Whether <paramref name="folder"/> is a copy <see cref="ContinueFromACopy"/> made.</summary>
  public static bool IsACopy(string folder)
  {
    var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    return string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
      Path.GetFileName(full).StartsWith(CopyPrefix, StringComparison.Ordinal);
  }

  // The copy can't delete itself while running: a detached cmd waits for it to exit, then removes it.
  private static void DeleteAfterExit(string folder)
  {
    // cmd.exe reads its own command line, so it gets one written for it rather than escaped argument by argument.
    Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 > nul & rmdir /s /q \"{folder}\"")
    {
      UseShellExecute = false,
      CreateNoWindow = true,
    });
  }
}
