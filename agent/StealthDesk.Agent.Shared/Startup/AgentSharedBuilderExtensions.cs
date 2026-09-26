using StealthDesk.Agent.Shared.Services;
using StealthDesk.Agent.Shared.Services.Windows;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Libraries.Shared.Services.Encryption;

namespace StealthDesk.Agent.Shared.Startup;

public static class AgentSharedBuilderExtensions
{
  public static IServiceCollection AddAgentSharedServices(this IServiceCollection services)
  {
    services.AddSingleton<IOptionsAccessor, OptionsAccessor>();
    services.AddSingleton<IFileSystemPathProvider, FileSystemPathProvider>();
    services.AddSingleton<IEd25519KeyProvider, Ed25519KeyProvider>();

    if (OperatingSystem.IsWindowsVersionAtLeast(8))
    {
      services.AddSingleton<IElevationChecker, ElevationCheckerWin>();
    }

    return services;
  }
}
