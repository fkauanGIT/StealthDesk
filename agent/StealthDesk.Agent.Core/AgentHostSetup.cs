using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StealthDesk.Agent.Core.Connection;
using StealthDesk.Agent.Core.Heartbeat;
using StealthDesk.Agent.Core.Settings;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Core;
using StealthDesk.Core.Security;
using StealthDesk.Hosting;
using StealthDesk.Observability;
using StealthDesk.Realtime;

namespace StealthDesk.Agent.Core;

/// <summary>How the agent process was started.</summary>
/// <param name="InstanceName">Separates several agents on one machine; null for the default instance.</param>
/// <param name="ServerUrl">Overrides <c>Agent:ServerUrl</c> from configuration.</param>
/// <param name="UseSettingsFile">Reads the instance's settings file (identity and key). Off in tests.</param>
public sealed record AgentStartup(string? InstanceName = null, Uri? ServerUrl = null, bool UseSettingsFile = true);

public static class AgentHostSetup
{
  /// <summary>
  /// Registers everything the agent needs except the platform inventory
  /// (<see cref="Inventory.IDeviceInventory"/> and <see cref="Inventory.ICpuLoad"/>).
  /// </summary>
  public static HostApplicationBuilder AddStealthDeskAgent(this HostApplicationBuilder builder, AgentStartup startup)
  {
    var paths = new AgentPaths(startup.InstanceName, AgentBuild.IsDebug);

    if (startup.ServerUrl is not null)
    {
      builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
      {
        [$"{AgentSettings.Section}:{nameof(AgentSettings.ServerUrl)}"] = startup.ServerUrl.ToString(),
      });
    }

    // Last source wins: what the agent saved about itself overrides the defaults shipped next to it.
    if (startup.UseSettingsFile)
    {
      builder.Configuration.AddJsonFile(paths.SettingsFile, optional: true, reloadOnChange: false);
    }

    var services = builder.Services;
    services.AddSingleton<IAgentPaths>(paths);
    services.Configure<AgentSettings>(builder.Configuration.GetSection(AgentSettings.Section));
    services.Configure<HeartbeatOptions>(options =>
      options.Interval = AgentBuild.IsDebug ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(5));

    services.AddSingleton(TimeProvider.System);
    services.AddSingleton<ISettingsStore, SettingsStore>();
    services.AddSingleton<IMessageSigner, MessageSigner>();
    services.AddRealtimeChannel<IAgentGateway, IAgentCallbacks, AgentCallbacks>();

    services.AddSingleton(new Backoff(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(15)));
    services.AddSingleton<HeartbeatService>();
    services.AddSingleton<IHeartbeat>(provider => provider.GetRequiredService<HeartbeatService>());
    services.AddHostedService(provider => provider.GetRequiredService<HeartbeatService>());
    services.AddHostedService<GatewayConnector>();
    services.AddHostedService<LifetimeLogging>();

    var deviceId = builder.Configuration.GetValue<Guid>($"{AgentSettings.Section}:{nameof(AgentSettings.DeviceId)}");
    builder.AddObservability(ServiceNames.Agent, deviceId == Guid.Empty ? null : deviceId.ToString());
    builder.AddDailyFileLog(paths.LogFile, keepDays: 7);

    return builder;
  }

  private sealed class AgentCallbacks : IAgentCallbacks;
}

internal static class AgentBuild
{
#if DEBUG
  public const bool IsDebug = true;
#else
  public const bool IsDebug = false;
#endif
}
