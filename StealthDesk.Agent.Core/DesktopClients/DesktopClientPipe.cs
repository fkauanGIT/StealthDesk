using System.IO.Pipes;
using StealthDesk.Core;

namespace StealthDesk.Agent.Core.DesktopClients;

/// <summary>The process on the other end of a pipe, as the operating system reports it.</summary>
public sealed record PipeClient(int ProcessId, string ExecutablePath);

/// <summary>Creates the server end of the desktop clients' pipe, with this platform's access rules.</summary>
public interface IDesktopClientPipeServer
{
  NamedPipeServerStream Create();
}

/// <summary>Asks the operating system which process connected to a pipe.</summary>
public interface IPipeClientIdentity
{
  Outcome<PipeClient> Identify(NamedPipeServerStream pipe);
}

/// <summary>Compares how programs are code-signed.</summary>
public interface IExecutableSigners
{
  /// <summary>
  /// Null when <paramref name="executable"/> carries the agent's own signing certificate, or when the agent isn't
  /// signed; otherwise why it doesn't.
  /// </summary>
  string? Mismatch(string executable);
}

/// <summary>Which programs may connect to the pipe.</summary>
/// <param name="Executable">The desktop client installed next to the agent.</param>
/// <param name="IsDebugBuild">
/// A debug build also takes <c>dotnet.exe</c> and the desktop client built in <paramref name="DebugFolder"/>, and
/// doesn't compare signatures.
/// </param>
/// <param name="DebugFolder">Where the desktop client project builds, when the agent runs from the source tree.</param>
public sealed record DesktopClientPipeRules(string Executable, bool IsDebugBuild, string? DebugFolder = null);
