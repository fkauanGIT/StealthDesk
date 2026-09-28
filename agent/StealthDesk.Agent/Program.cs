using StealthDesk.Agent.Startup;
using System.CommandLine;

var rootCommand = new RootCommand("Open-source remote control agent.")
{
  CommandProvider.GetRunCommand(args),
};

var parseResult = rootCommand.Parse(args);
return await parseResult.InvokeAsync();
