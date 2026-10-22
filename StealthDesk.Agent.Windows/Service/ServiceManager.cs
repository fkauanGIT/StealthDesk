using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace StealthDesk.Agent.Windows.Service;

/// <summary>Creates, starts, stops and deletes the agent's Windows service, through sc.exe and the service manager.</summary>
[SupportedOSPlatform("windows")]
public static class ServiceManager
{
  private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(60);

  public static bool Exists(string serviceName)
  {
    using var service = Find(serviceName);
    return service is not null;
  }

  /// <summary>
  /// An automatic service running as LocalSystem. Windows restarts it 5 seconds after it fails, whether it crashed or
  /// stopped with an error.
  /// </summary>
  public static void Create(AgentInstallation installation, string description)
  {
    var name = installation.ServiceName;
    Sc("create", name, "binPath=", installation.ServiceCommandLine, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", installation.DisplayName);
    Sc("description", name, description);
    Sc("failure", name, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/5000");
    Sc("failureflag", name, "1");
  }

  public static void Start(string serviceName)
  {
    using var service = Find(serviceName) ?? throw new InvalidOperationException($"The service {serviceName} doesn't exist.");
    if (service.Status != ServiceControllerStatus.Running)
    {
      service.Start();
    }

    service.WaitForStatus(ServiceControllerStatus.Running, StatusTimeout);
  }

  public static void Stop(string serviceName)
  {
    using var service = Find(serviceName);
    if (service is null || service.Status == ServiceControllerStatus.Stopped)
    {
      return;
    }

    if (service.Status != ServiceControllerStatus.StopPending)
    {
      service.Stop();
    }

    service.WaitForStatus(ServiceControllerStatus.Stopped, StatusTimeout);
  }

  /// <summary>Deletes the service and waits until Windows has removed it.</summary>
  public static void Delete(string serviceName)
  {
    if (!Exists(serviceName))
    {
      return;
    }

    Sc("delete", serviceName);
    var deadline = DateTime.UtcNow + StatusTimeout;
    while (Exists(serviceName))
    {
      if (DateTime.UtcNow > deadline)
      {
        throw new InvalidOperationException($"Windows didn't remove the service {serviceName} within {StatusTimeout.TotalSeconds} seconds.");
      }

      Thread.Sleep(250);
    }
  }

  // A controller for a missing service only fails once it's asked something.
  private static ServiceController? Find(string serviceName)
  {
    var service = new ServiceController(serviceName);
    try
    {
      _ = service.Status;
      return service;
    }
    catch (InvalidOperationException)
    {
      service.Dispose();
      return null;
    }
  }

  private static void Sc(params string[] arguments)
  {
    var start = new ProcessStartInfo("sc.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in arguments)
    {
      start.ArgumentList.Add(argument);
    }

    using var process = Process.Start(start) ?? throw new InvalidOperationException("sc.exe didn't start.");
    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
      throw new InvalidOperationException($"sc.exe {arguments[0]} failed ({process.ExitCode}): {output.Trim()}");
    }
  }
}
