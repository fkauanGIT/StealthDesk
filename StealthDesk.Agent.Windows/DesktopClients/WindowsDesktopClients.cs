using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using StealthDesk.Agent.Core.DesktopClients;
using StealthDesk.Branding;
using StealthDesk.Contracts.Agents;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace StealthDesk.Agent.Windows.DesktopClients;

[SupportedOSPlatform("windows6.1")]
internal sealed class WindowsDesktopSessions : IDesktopSessions
{
  public IReadOnlyList<DesktopSession> Current() => WindowsSystem.DesktopSessions();
}

/// <summary>
/// Starts the desktop client installed next to the agent in a session, as LocalSystem inside that session: with the
/// token of the session's <c>winlogon.exe</c>, which carries the session, on its interactive desktop. That's what lets
/// it later reach the sign-in screen and secure screens such as UAC, which belong to no user.
/// </summary>
[SupportedOSPlatform("windows6.1")]
internal sealed unsafe class WindowsDesktopClientLauncher(string executable, DesktopClientStart start, ILogger<WindowsDesktopClientLauncher> logger)
  : IDesktopClientLauncher
{
  private const uint MaximumAllowed = 0x02000000;

  public string? Missing() => File.Exists(executable) ? null : $"{executable} isn't there.";

  public IDesktopClientProcess? Start(int sessionId)
  {
    var winlogon = Process.GetProcessesByName("winlogon");
    var inSession = winlogon.FirstOrDefault(x => x.SessionId == sessionId);
    var winlogonId = inSession?.Id;
    foreach (var process in winlogon)
    {
      process.Dispose();
    }

    if (winlogonId is null)
    {
      logger.LogWarning("Session {SessionId} has no winlogon.exe to start a desktop client with.", sessionId);
      return null;
    }

    using var winlogonHandle = PInvoke.OpenProcess_SafeHandle((PROCESS_ACCESS_RIGHTS)MaximumAllowed, false, (uint)winlogonId);
    if (winlogonHandle.IsInvalid)
    {
      return Failed("OpenProcess(winlogon)", sessionId);
    }

    if (!PInvoke.OpenProcessToken(winlogonHandle, TOKEN_ACCESS_MASK.TOKEN_DUPLICATE, out var winlogonToken))
    {
      return Failed("OpenProcessToken(winlogon)", sessionId);
    }

    using (winlogonToken)
    {
      if (!PInvoke.DuplicateTokenEx(
        winlogonToken,
        (TOKEN_ACCESS_MASK)MaximumAllowed,
        null,
        SECURITY_IMPERSONATION_LEVEL.SecurityIdentification,
        TOKEN_TYPE.TokenPrimary,
        out var token))
      {
        return Failed("DuplicateTokenEx", sessionId);
      }

      using (token)
      {
        return Launch(token, sessionId);
      }
    }
  }

  public IReadOnlyList<IDesktopClientProcess> Running()
  {
    var running = new List<IDesktopClientProcess>();
    foreach (var process in Process.GetProcessesByName(Brand.DesktopClientName))
    {
      if (IsOurs(process))
      {
        running.Add(new DesktopClientProcess(process));
      }
      else
      {
        process.Dispose();
      }
    }

    return running;
  }

  private IDesktopClientProcess? Launch(SafeFileHandle token, int sessionId)
  {
    var desktop = @"winsta0\default".ToCharArray().Append('\0').ToArray();
    var commandLine = $"\"{executable}\" {DesktopClientCommandLine.Arguments(start)}\0".ToCharArray().AsSpan();
    fixed (char* desktopName = desktop)
    {
      var startup = new STARTUPINFOW
      {
        cb = (uint)sizeof(STARTUPINFOW),
        lpDesktop = desktopName,
      };

      if (!PInvoke.CreateProcessAsUser(
        token,
        executable,
        ref commandLine,
        null,
        null,
        false,
        PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT | PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW,
        null,
        Path.GetDirectoryName(executable),
        in startup,
        out var started))
      {
        return Failed("CreateProcessAsUser", sessionId);
      }

      PInvoke.CloseHandle(started.hThread);
      PInvoke.CloseHandle(started.hProcess);
      try
      {
        return new DesktopClientProcess(Process.GetProcessById((int)started.dwProcessId));
      }
      catch (ArgumentException)
      {
        // It exited already.
        return null;
      }
    }
  }

  private bool IsOurs(Process process)
  {
    try
    {
      return string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
      // Gone already, or not ours to inspect.
      return false;
    }
  }

  private IDesktopClientProcess? Failed(string call, int sessionId)
  {
    logger.LogWarning("{Call} failed for session {SessionId}: {Error}", call, sessionId, Marshal.GetLastPInvokeErrorMessage());
    return null;
  }

  private sealed class DesktopClientProcess(Process process) : IDesktopClientProcess
  {
    public int Id => process.Id;

    public int SessionId => process.SessionId;

    public bool HasExited => process.HasExited;

    public void Stop()
    {
      try
      {
        process.Kill(entireProcessTree: true);
        process.WaitForExit(TimeSpan.FromSeconds(10));
      }
      catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
      {
        // Gone already.
      }
    }

    public void Dispose() => process.Dispose();
  }
}
