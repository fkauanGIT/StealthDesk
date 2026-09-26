using StealthDesk.Agent.Common.Models;
using StealthDesk.Agent.Common.Services;
using StealthDesk.Agent.Shared.Startup;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StealthDesk.Web.ServiceDefaults;
using StealthDesk.Libraries.NativeInterop.Windows;
using StealthDesk.Libraries.Api.Contracts.Hubs.Clients;
using StealthDesk.Libraries.Shared.Helpers;
using StealthDesk.Libraries.DataRedaction;
using StealthDesk.Libraries.Hosting;
using StealthDesk.Libraries.Serilog;
using StealthDesk.Libraries.Signalr.Client.Extensions;
using StealthDesk.Libraries.Shared.Services.FileSystem;

namespace StealthDesk.Agent.Common.Startup;

internal static class HostApplicationBuilderExtensions
{
  internal static HostApplicationBuilder AddStealthDeskAgent(
    this HostApplicationBuilder builder,
    StartupMode startupMode,
    string? instanceId,
    Uri? serverUri,
    bool loadAppSettings = true)
  {

    instanceId = instanceId?.SanitizeForFileSystem();
    var services = builder.Services;
    var configuration = builder.Configuration;

    // Prevent reading for appsettings in same directory as the executable.
    // We want it to use the custom path, which we set further below.
    if (!SystemEnvironment.Instance.IsDebug)
    {
      configuration.Sources.Clear();
    }

    builder.Configuration
      .AddInMemoryCollection(new Dictionary<string, string?>
      {
        { $"{InstanceOptions.SectionKey}:{nameof(InstanceOptions.InstanceId)}", instanceId },
        { $"{AgentAppOptions.SectionKey}:{nameof(AgentAppOptions.ServerUri)}", serverUri?.ToString() },
      })
      .AddEnvironmentVariables();

    services
      .AddOptions<AgentAppOptions>()
      .Bind(configuration.GetSection(AgentAppOptions.SectionKey));

    services
      .AddOptions<InstanceOptions>()
      .Bind(configuration.GetSection(InstanceOptions.SectionKey));

    var pathProvider = GetTempPathProvider(builder);

    if (loadAppSettings)
    {
      builder.Configuration.AddJsonFile(pathProvider.GetAgentAppSettingsPath(), true, true);
    }

    var appOptions = builder.Configuration
      .GetSection(AgentAppOptions.SectionKey)
      .Get<AgentAppOptions>() ?? new AgentAppOptions();

    builder.Services.AddStarRedactor();
    services.AddAgentSharedServices();
    services.AddSingleton<ISystemEnvironment>(_ => SystemEnvironment.Instance);
    services.AddSingleton<IFileSystem, FileSystem>();
    services.AddTransient<IHubConnectionBuilder, HubConnectionBuilder>();
    services.AddSingleton(TimeProvider.System);
    services.AddSingleton<IAgentHeartbeatTimer, AgentHeartbeatTimer>();
    services.AddStronglyTypedSignalrClient<IAgentHub, IAgentHubClient, AgentHubClient>(ServiceLifetime.Singleton);

    if (OperatingSystem.IsWindowsVersionAtLeast(8))
    {
      services.AddSingleton<IWin32Interop, Win32Interop>();
      services.AddSingleton<IDeviceInfoProvider, DeviceInfoProviderWin>();
      services.AddSingleton<ICpuUtilizationSampler, CpuUtilizationSampler>();
    }
    else
    {
      throw new PlatformNotSupportedException();
    }

    // Add services only needed when running.
    if (startupMode == StartupMode.Run)
    {
      services.AddHostedService<HubConnectionInitializer>();
      services.AddHostedService(x => x.GetRequiredService<IAgentHeartbeatTimer>());
      services.AddHostedService<HostLifetimeEventResponder>();
      services.AddHostedService(s => s.GetRequiredService<ICpuUtilizationSampler>());
    }

    var deviceId = appOptions.DeviceId == Guid.Empty
      ? null
      : appOptions.DeviceId.ToString();

    builder.AddServiceDefaults(ServiceNames.StealthdeskAgent, hostId: deviceId);
    builder.BootstrapSerilog(pathProvider.GetAgentLogFilePath(), TimeSpan.FromDays(7));

    return builder;
  }

  private static FileSystemPathProvider GetTempPathProvider(HostApplicationBuilder builder)
  {
    var instanceOptions = builder.Configuration
      .GetSection(InstanceOptions.SectionKey)
      .Get<InstanceOptions>() ?? new InstanceOptions();

    IElevationChecker elevationChecker =
      SystemEnvironment.Instance.IsWindows()
        ? new ElevationCheckerWin()
        : throw new PlatformNotSupportedException();

    return new FileSystemPathProvider(
      SystemEnvironment.Instance,
      elevationChecker,
      new FileSystem(new SerilogLogger<FileSystem>()),
      new OptionsMonitorWrapper<InstanceOptions>(instanceOptions));
  }
}
