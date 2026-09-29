using System.CommandLine;
using Microsoft.Extensions.Hosting;
using StealthDesk.Agent.Core;
using StealthDesk.Agent.Core.Settings;
using StealthDesk.Agent.Windows;
using StealthDesk.Branding;

namespace StealthDesk.Agent;

/// <summary>
/// <c>stealthdesk-agent run [--instance name] [--server url]</c>. The executable only parses the command line;
/// the behavior lives in the libraries so it can be tested without starting a process.
/// </summary>
internal static class AgentCommandLine
{
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
    };

    var run = new Command("run", $"Connect to the {Brand.Name} server and keep reporting this device.")
    {
      instance,
      server,
    };

    run.SetAction(async (parse, cancellationToken) =>
    {
      if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
      {
        Console.Error.WriteLine("The agent currently runs on Windows only.");
        return 1;
      }

      var builder = Host.CreateApplicationBuilder(args);
      builder.AddStealthDeskAgent(new AgentStartup(parse.GetValue(instance), parse.GetValue(server)));
      builder.Services.AddWindowsInventory();

      using var host = builder.Build();
      await host.RunAsync(cancellationToken);
      return 0;
    });

    return new RootCommand($"{Brand.Name} agent.") { run };
  }
}
