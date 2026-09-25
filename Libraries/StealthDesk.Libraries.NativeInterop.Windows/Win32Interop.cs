// ReSharper disable IdentifierTypo
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using StealthDesk.Libraries.Api.Contracts.Dtos.Devices;
using Microsoft.Extensions.Logging;
using WTS_INFO_CLASS = Windows.Win32.System.RemoteDesktop.WTS_INFO_CLASS;

namespace StealthDesk.Libraries.NativeInterop.Windows;

public interface IWin32Interop
{
  List<DesktopSession> GetActiveSessions();
  string GetUsernameFromSessionId(uint sessionId);
  bool GlobalMemoryStatus(ref MemoryStatusEx lpBuffer);
}

[SupportedOSPlatform("windows6.1")]
public unsafe partial class Win32Interop(ILogger<Win32Interop> logger) : IWin32Interop
{
  private readonly ILogger<Win32Interop> _logger = logger;

  public List<DesktopSession> GetActiveSessions()
  {
    var sessions = new List<DesktopSession>();
    var consoleSessionId = PInvoke.WTSGetActiveConsoleSessionId();
    sessions.Add(new DesktopSession
    {
      SystemSessionId = (int)consoleSessionId,
      Type = DesktopSessionType.Console,
      Name = "Console",
      Username = GetUsernameFromSessionId(consoleSessionId),
      DesktopName = ResolveDesktopName(consoleSessionId)
    });

    var ppSessionInfo = nint.Zero;
    var count = 0;
    var enumSessionResult =
      WtsApi32.WTSEnumerateSessions(WtsApi32.WtsCurrentServerHandle, 0, 1, ref ppSessionInfo, ref count);
    var dataSize = Marshal.SizeOf<WtsApi32.WtsSessionInfo>();
    var current = ppSessionInfo;

    if (enumSessionResult == 0)
    {
      return sessions;
    }

    for (var i = 0; i < count; i++)
    {
      try
      {
        var sessionInfo = Marshal.PtrToStructure<WtsApi32.WtsSessionInfo>(current);
        current += dataSize;
        if (sessionInfo.State == WtsApi32.WtsConnectstateClass.WtsActive && sessionInfo.SessionID != consoleSessionId)
        {
          sessions.Add(new DesktopSession
          {
            SystemSessionId = (int)sessionInfo.SessionID,
            Name = sessionInfo.pWinStationName,
            Type = DesktopSessionType.Rdp,
            Username = GetUsernameFromSessionId(sessionInfo.SessionID),
            DesktopName = ResolveDesktopName(sessionInfo.SessionID)
          });
        }
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to marshal active session.");
      }
    }

    WtsApi32.WTSFreeMemory(ppSessionInfo);

    return sessions;
  }

  public string GetUsernameFromSessionId(uint sessionId)
  {
    var result = PInvoke.WTSQuerySessionInformation(HANDLE.Null, sessionId, WTS_INFO_CLASS.WTSUserName,
      out var username, out var bytesReturned);

    if (result && bytesReturned > 1)
    {
      return username.ToString();
    }

    return string.Empty;
  }

  public bool GlobalMemoryStatus(ref MemoryStatusEx lpBuffer)
  {
    return GlobalMemoryStatusEx(ref lpBuffer);
  }

  private static string ResolveDesktopName(uint targetSessionId)
  {
    var isLogonScreenVisible = Process
      .GetProcessesByName("LogonUI")
      .Any(x => x.SessionId == targetSessionId);

    var isSecureDesktopVisible = Process
      .GetProcessesByName("consent")
      .Any(x => x.SessionId == targetSessionId);

    if (isLogonScreenVisible || isSecureDesktopVisible)
    {
      return "Winlogon";
    }

    return "Default";
  }

  [return: MarshalAs(UnmanagedType.Bool)]
  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
