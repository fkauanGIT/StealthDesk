using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StealthDesk.Agent.Core.Connection;
using StealthDesk.Agent.Core.DesktopClients;
using StealthDesk.Agent.Core.Heartbeat;
using StealthDesk.Agent.Core.Settings;
using StealthDesk.Agent.Core.Terminal;
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
/// <param name="InstallerKey">Overrides <c>Agent:InstallerKey</c>: the key that lets a new device join.</param>
/// <param name="DeviceId">The id a new device asks for; one it already saved wins.</param>
public sealed record AgentStartup(
  string? InstanceName = null,
  Uri? ServerUrl = null,
  bool UseSettingsFile = true,
  string? InstallerKey = null,
  Guid? DeviceId = null);

public static class AgentHostSetup
{
  /// <summary>
  /// Registers everything the agent needs except the platform inventory
  /// (<see cref="Inventory.IDeviceInventory"/> and <see cref="Inventory.ICpuLoad"/>).
  /// </summary>
  public static HostApplicationBuilder AddStealthDeskAgent(this HostApplicationBuilder builder, AgentStartup startup)
  {
    var paths = new AgentPaths(startup.InstanceName, AgentBuild.IsDebug);

    var overrides = new Dictionary<string, string?>();
    if (!string.IsNullOrWhiteSpace(startup.InstallerKey))
    {
      overrides[$"{AgentSettings.Section}:{nameof(AgentSettings.InstallerKey)}"] = startup.InstallerKey.Trim();
    }

    if (startup.DeviceId is { } requestedId && requestedId != Guid.Empty)
    {
      overrides[$"{AgentSettings.Section}:{nameof(AgentSettings.DeviceId)}"] = requestedId.ToString();
    }

    builder.Configuration.AddInMemoryCollection(overrides);

    // Last source wins: what the agent saved about itself overrides the defaults shipped next to it.
    if (startup.UseSettingsFile)
    {
      builder.Configuration.AddJsonFile(paths.SettingsFile, optional: true, reloadOnChange: false);
    }

    // A server given on the command line wins even over the saved one: that's how a device moves to a new address.
    if (startup.ServerUrl is not null)
    {
      builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
      {
        [$"{AgentSettings.Section}:{nameof(AgentSettings.ServerUrl)}"] = startup.ServerUrl.ToString(),
      });
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
    services.AddSingleton<ITerminalOutputSink, GatewayTerminalSink>();
    services.AddSingleton<TerminalSessions>();
    services.AddSingleton<ITerminalSessions>(provider => provider.GetRequiredService<TerminalSessions>());
    // Empty unless the platform serves the desktop clients' pipe: remote control then finds no session to show.
    services.AddSingleton<DesktopClientConnections>();
    services.AddSingleton<DesktopClientRemoteControl>();

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
}

/// <summary>Whether this is a debug build, which keeps its own folders and service so it never touches an installed agent.</summary>
public static class AgentBuild
{
#if DEBUG
  public const bool IsDebug = true;
#else
  public const bool IsDebug = false;
#endif
}
