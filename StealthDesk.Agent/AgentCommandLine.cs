using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StealthDesk.Agent.Core;
using StealthDesk.Agent.Core.Heartbeat;
using StealthDesk.Agent.Windows;
using StealthDesk.Agent.Windows.Service;
using StealthDesk.Branding;
using StealthDesk.Contracts.Agents;

namespace StealthDesk.Agent;

/// <summary>
/// <c>run</c> keeps reporting this device, in a console or as the Windows service; <c>install</c> and <c>uninstall</c>
/// add and remove that service. The executable only parses the command line; the behavior lives in the libraries so
/// it can be tested without starting a process.
/// </summary>
internal static class AgentCommandLine
{
  /// <summary>How long install waits for the server to accept the device before giving up.</summary>
  private static readonly TimeSpan JoinTimeout = TimeSpan.FromMinutes(2);

  public static RootCommand Create(string[] args)
  {
    var instance = new Option<string?>("--instance", "-i")
    {
      Description = "Name of this agent instance, to run more than one agent on the same machine.",
    };
    instance.Validators.Add(result =>
    {
      if (InstanceNames.Validate(result.GetValueOrDefault<string?>()) is { } error)
      {
        result.AddError(error);
      }
    });

    var server = new Option<Uri?>("--server", "-s")
    {
      Description = "Server address, e.g. http://localhost:5099. Overrides the configured one.",
      // System.CommandLine has no conversion to Uri of its own.
      CustomParser = result =>
      {
        // Without a value, System.CommandLine reports the missing argument itself.
        if (result.Tokens is not [{ Value: var text }])
        {
          return null;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var address) && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps))
        {
          return address;
        }

        result.AddError($"--server must be an absolute http or https address, such as http://localhost:5099, not '{text}'.");
        return null;
      },
    };

    var installerKey = new Option<string?>("--installer-key", "-k")
    {
      Description = "Installer key ({id}:{secret}) that lets this device join the server. Only needed until it joins.",
    };

    var deviceId = new Option<Guid?>("--device-id")
    {
      Description = "Id for a new device. By default the server picks one; a device that already joined keeps its own.",
    };

    var run = new Command("run", $"Connect to the {Brand.Name} server and keep reporting this device.")
    {
      instance,
      server,
      installerKey,
      deviceId,
    };

    run.SetAction(async (parse, cancellationToken) =>
    {
      if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
      {
        Console.Error.WriteLine("The agent currently runs on Windows only.");
        return 1;
      }

      var startup = new AgentStartup(parse.GetValue(instance), parse.GetValue(server), InstallerKey: parse.GetValue(installerKey), DeviceId: parse.GetValue(deviceId));
      using var host = BuildAgent(args, startup);
      await host.RunAsync(cancellationToken);
      return 0;
    });

    var install = new Command("install", $"Install this agent as a Windows service that joins the {Brand.Name} server. Needs an administrator.")
    {
      instance,
      server,
      installerKey,
      deviceId,
    };
    install.Validators.Add(result =>
    {
      if (result.GetValue(server) is null && result.GetResult(server) is null)
      {
        result.AddError("install needs --server, the address of the server this device joins.");
      }

      if (string.IsNullOrWhiteSpace(result.GetValue(installerKey)))
      {
        result.AddError("install needs --installer-key, the key that lets this device join the server.");
      }
    });

    install.SetAction(async (parse, cancellationToken) =>
    {
      if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
      {
        Console.Error.WriteLine("The agent currently installs on Windows only.");
        return 1;
      }

      var installer = Installer(parse.GetValue(instance), parse.InvocationConfiguration.Output);
      if (installer.Tool.RunsFromInstallFolder(AppContext.BaseDirectory))
      {
        return installer.Tool.ContinueFromACopy(Executable, args);
      }

      // The instance as given: the default one has no name of its own to pass on.
      var startup = new AgentStartup(parse.GetValue(instance), parse.GetValue(server), InstallerKey: parse.GetValue(installerKey), DeviceId: parse.GetValue(deviceId));
      return await installer.Tool.InstallAsync(Executable, token => JoinAsync(args, startup, token), cancellationToken);
    });

    var uninstall = new Command("uninstall", $"Remove this agent's Windows service, its files and the device's identity. Needs an administrator.")
    {
      instance,
    };

    uninstall.SetAction(async (parse, cancellationToken) =>
    {
      if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
      {
        Console.Error.WriteLine("The agent currently installs on Windows only.");
        return 1;
      }

      var installer = Installer(parse.GetValue(instance), parse.InvocationConfiguration.Output);
      if (installer.Tool.RunsFromInstallFolder(AppContext.BaseDirectory))
      {
        return installer.Tool.ContinueFromACopy(Executable, args);
      }

      return await installer.Tool.UninstallAsync(AppContext.BaseDirectory);
    });

    return new RootCommand($"{Brand.Name} agent.") { run, install, uninstall };
  }

  [System.Runtime.Versioning.SupportedOSPlatform("windows6.1")]
  private static IHost BuildAgent(string[] args, AgentStartup startup)
  {
    // As a service the working folder is System32: settings files are found next to the executable instead.
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
    builder.AddStealthDeskAgent(startup);
    builder.Services.AddWindowsInventory();
    builder.Services.AddWindowsService(options => options.ServiceName = Installation(startup.InstanceName).ServiceName);
    return builder.Build();
  }

  /// <summary>Runs the agent until the server first answers it: null when it accepted the device, otherwise why not.</summary>
  private static async Task<string?> JoinAsync(string[] args, AgentStartup startup, CancellationToken cancellationToken)
  {
    if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
    {
      throw new PlatformNotSupportedException("The agent currently runs on Windows only.");
    }

    using var host = BuildAgent(args, startup);
    await host.StartAsync(cancellationToken);
    try
    {
      var answer = host.Services.GetRequiredService<IHeartbeat>().FirstAnswer;
      return await answer.WaitAsync(JoinTimeout, cancellationToken);
    }
    catch (TimeoutException)
    {
      return $"no answer from {startup.ServerUrl} within {JoinTimeout.TotalMinutes} minutes. Check the address and that the server is running.";
    }
    finally
    {
      await host.StopAsync(CancellationToken.None);
    }
  }

  private static string Executable => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, $"{Brand.AgentName}.exe");

  private static AgentInstallation Installation(string? instanceName) =>
    new(instanceName, AgentBuild.IsDebug, Path.GetFileName(Environment.ProcessPath) ?? $"{Brand.AgentName}.exe");

  [System.Runtime.Versioning.SupportedOSPlatform("windows")]
  private static (AgentInstallation Installation, AgentInstaller Tool) Installer(string? instanceName, TextWriter? output)
  {
    var installation = Installation(instanceName);
    return (installation, new AgentInstaller(installation, output ?? Console.Out));
  }
}
