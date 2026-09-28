using System.CommandLine;
using System.CommandLine.Parsing;
using StealthDesk.Agent.Common.Models;
using StealthDesk.Agent.Common.Startup;
using StealthDesk.Libraries.Branding;
using StealthDesk.Libraries.Shared.DataValidation;
using Microsoft.Extensions.Hosting;

namespace StealthDesk.Agent.Startup;

internal static class CommandProvider
{
  internal static Command GetRunCommand(string[] args)
  {
    var instanceIdOption = CreateInstanceIdOption();

    var runCommand = new Command("run", $"Run the {BrandingConstants.BrandName} service.")
    {
      instanceIdOption
    };

    runCommand.SetAction(async parseResult =>
    {
      var instanceId = parseResult.GetValue(instanceIdOption);
      using var host = CreateHost(StartupMode.Run, args, instanceId);
      await host.RunAsync();
    });

    return runCommand;
  }

  private static IHost CreateHost(
    StartupMode startupMode,
    string[] args,
    string? instanceId = null)
  {
    var host = Host.CreateApplicationBuilder(args);

    host.AddStealthDeskAgent(startupMode, instanceId, serverUri: null);
    return host.Build();
  }

  private static Option<string?> CreateInstanceIdOption()
  {
    var instanceIdOption = new Option<string?>("-i", "--instance-id")
    {
      Description = "The instance ID of the agent, which can be used for multiple agent installations."
    };

    instanceIdOption.Validators.Add(ValidateInstanceId);
    return instanceIdOption;
  }

  private static void ValidateInstanceId(OptionResult optionResult)
  {
    var id = optionResult.GetValueOrDefault<string?>();
    var validationError = Validators.ValidateInstanceId(id);
    if (validationError is not null)
    {
      optionResult.AddError(validationError);
    }
  }
}
