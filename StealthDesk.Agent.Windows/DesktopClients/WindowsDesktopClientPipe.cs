using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using StealthDesk.Agent.Core.DesktopClients;
using StealthDesk.Core;
using StealthDesk.Ipc;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace StealthDesk.Agent.Windows.DesktopClients;

/// <summary>
/// The pipe's server end. Run as the service, in session 0, only LocalSystem and the users logged on at a desktop may
/// open it, and anonymous connections are denied. Run in a user's session, as when debugging, it keeps the default
/// access of whoever started the agent.
/// </summary>
[SupportedOSPlatform("windows6.1")]
internal sealed class WindowsDesktopClientPipeServer(string pipeName, int sessionId) : IDesktopClientPipeServer
{
  public NamedPipeServerStream Create() => sessionId == 0 ? Pipes.CreateServer(pipeName, Security()) : Pipes.CreateServer(pipeName);

  public static PipeSecurity Security()
  {
    var security = new PipeSecurity();
    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
    // Read and write only: creating another instance of the pipe would let a user's program pose as the agent.
    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AnonymousSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
    return security;
  }
}

/// <summary>The process on the other end of a pipe and its executable, as Windows reports them.</summary>
[SupportedOSPlatform("windows6.1")]
internal sealed class WindowsPipeClientIdentity : IPipeClientIdentity
{
  public Outcome<PipeClient> Identify(NamedPipeServerStream pipe)
  {
    if (!PInvoke.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
    {
      return Outcome.Failure<PipeClient>($"GetNamedPipeClientProcessId failed: {Marshal.GetLastPInvokeErrorMessage()}");
    }

    if (processId == 0)
    {
      return Outcome.Failure<PipeClient>("Windows reported process 0 behind the pipe.");
    }

    using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
    if (process.IsInvalid)
    {
      return Outcome.Failure<PipeClient>($"Process {processId} couldn't be opened: {Marshal.GetLastPInvokeErrorMessage()}");
    }

    var buffer = new char[32768];
    var length = (uint)buffer.Length;
    if (!PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref length))
    {
      return Outcome.Failure<PipeClient>($"The executable of process {processId} couldn't be read: {Marshal.GetLastPInvokeErrorMessage()}");
    }

    return Outcome.Success(new PipeClient((int)processId, new string(buffer, 0, (int)length)));
  }
}

/// <summary>Compares the Authenticode signing certificate of a program with the agent's own.</summary>
/// <param name="agentExecutable">The agent's executable, whose certificate the others must carry.</param>
[SupportedOSPlatform("windows6.1")]
internal sealed class WindowsExecutableSigners(string agentExecutable) : IExecutableSigners
{
  public string? Mismatch(string executable)
  {
    var expected = Thumbprint(agentExecutable);
    if (expected is null)
    {
      return null;
    }

    var actual = Thumbprint(executable);
    if (actual is null)
    {
      return $"{executable} isn't signed, while the agent is.";
    }

    return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
      ? null
      : $"{executable} is signed with certificate {actual}, not the agent's {expected}.";
  }

  /// <summary>The thumbprint of the certificate that signed <paramref name="executable"/>, or null when it isn't signed.</summary>
  public static string? Thumbprint(string executable)
  {
    try
    {
      if (X509Certificate2.GetCertContentType(executable) != X509ContentType.Authenticode)
      {
        return null;
      }

#pragma warning disable SYSLIB0057 // The replacement loaders read certificate files, not the signature inside a program.
      using var certificate = X509Certificate.CreateFromSignedFile(executable);
#pragma warning restore SYSLIB0057
      return certificate.GetCertHashString();
    }
    catch (CryptographicException)
    {
      return null;
    }
  }
}

/// <summary>Where the desktop client is, for the launcher and for the pipe.</summary>
internal static class DesktopClientFiles
{
  /// <summary>The desktop client installed next to the agent.</summary>
  public static string Executable { get; } =
    Path.Combine(AppContext.BaseDirectory, Branding.Brand.DesktopClientFolderName, $"{Branding.Brand.DesktopClientName}.exe");

  /// <summary>Where the desktop client project builds in debug, when the agent runs from the source tree; otherwise null.</summary>
  public static string? DebugFolder()
  {
    for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
    {
      if (File.Exists(Path.Combine(folder.FullName, "StealthDesk.slnx")))
      {
        return Path.Combine(folder.FullName, "StealthDesk.DesktopClient", "bin", "Debug");
      }
    }

    return null;
  }

  public static int CurrentSessionId()
  {
    using var process = Process.GetCurrentProcess();
    return process.SessionId;
  }
}
