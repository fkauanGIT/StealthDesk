#:project ../../shared/StealthDesk.Contracts/StealthDesk.Contracts.csproj

// Prints the install command the Add device page copies, built by the same code, for a server and an installer key.
// Usage: dotnet run .github/scripts/install-command.cs -- <server> <installer key> <download path> <file name> <size> <sha256>
using StealthDesk.Contracts.Agents;

if (args.Length != 6)
{
  Console.Error.WriteLine("Usage: install-command.cs <server> <installer key> <download path> <file name> <size> <sha256>");
  return 1;
}

var download = new AgentDownload(args[2], args[3], long.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture), args[5]);
Console.WriteLine(AgentInstallCommand.Windows(new Uri(args[0]), download, args[1]));
return 0;
