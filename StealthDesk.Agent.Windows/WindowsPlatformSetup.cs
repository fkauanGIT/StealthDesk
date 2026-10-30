using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StealthDesk.Agent.Core;
using StealthDesk.Agent.Core.DesktopClients;
using StealthDesk.Agent.Core.Settings;
using StealthDesk.Agent.Core.Inventory;
using StealthDesk.Agent.Windows.DesktopClients;
using StealthDesk.Branding;
using StealthDesk.Contracts.Agents;
using StealthDesk.Ipc;

namespace StealthDesk.Agent.Windows;

public static class WindowsPlatformSetup
{
  /// <summary>Inventory and CPU measurement backed by Windows APIs.</summary>
  [SupportedOSPlatform("windows6.1")]
  public static IServiceCollection AddWindowsInventory(this IServiceCollection services)
  {
    services.AddSingleton<WindowsCpuLoad>();
    services.AddSingleton<ICpuLoad>(provider => provider.GetRequiredService<WindowsCpuLoad>());
    services.AddHostedService(provider => provider.GetRequiredService<WindowsCpuLoad>());
    services.AddSingleton<IDeviceInventory, WindowsInventory>();
    return services;
  }

  /// <summary>
  /// A desktop client in every session, kept running by <see cref="DesktopClientWatcher"/>. Only for an agent running as
  /// LocalSystem, the service: nothing else may start a process inside another session.
  /// </summary>
  [SupportedOSPlatform("windows6.1")]
  public static IServiceCollection AddWindowsDesktopClients(this IServiceCollection services)
  {
    services.AddSingleton<IDesktopSessions, WindowsDesktopSessions>();
    services.AddSingleton<IDesktopClientLauncher>(provider =>
    {
      var paths = provider.GetRequiredService<IAgentPaths>();
      return new WindowsDesktopClientLauncher(
        DesktopClientFiles.Executable,
        new DesktopClientStart(paths.InstanceName, paths.InstanceFolder),
        provider.GetRequiredService<ILogger<WindowsDesktopClientLauncher>>());
    });
    services.AddHostedService<DesktopClientWatcher>();
    return services;
  }

  /// <summary>
  /// The pipe the desktop clients talk to the agent through, served by <see cref="DesktopClientPipeHost"/>. Served
  /// however the agent runs, so a debug client started by hand can connect to a debug agent.
  /// </summary>
  [SupportedOSPlatform("windows6.1")]
  public static IServiceCollection AddWindowsDesktopClientPipe(this IServiceCollection services)
  {
    services.AddSingleton<IDesktopClientPipeServer>(provider => new WindowsDesktopClientPipeServer(
      Pipes.NameFor(provider.GetRequiredService<IAgentPaths>().InstanceName, AgentBuild.IsDebug),
      DesktopClientFiles.CurrentSessionId()));
    services.AddSingleton<IPipeClientIdentity, WindowsPipeClientIdentity>();
    services.AddSingleton<IExecutableSigners>(new WindowsExecutableSigners(Environment.ProcessPath ?? DesktopClientFiles.Executable));
    services.AddSingleton(new DesktopClientPipeRules(
      DesktopClientFiles.Executable,
      AgentBuild.IsDebug,
      AgentBuild.IsDebug ? DesktopClientFiles.DebugFolder() : null));
    services.AddSingleton<DesktopClientAuthenticator>();
    services.AddHostedService<DesktopClientPipeHost>();
    return services;
  }
}
