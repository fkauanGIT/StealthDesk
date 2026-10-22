using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Agent.Core.Inventory;

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
}
