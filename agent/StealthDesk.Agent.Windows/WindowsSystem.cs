using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StealthDesk.Agent.Core.Inventory;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.RemoteDesktop;
using Windows.Win32.System.SystemInformation;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace StealthDesk.Agent.Windows;

/// <summary>Thin, safe wrappers over the Win32 calls the agent needs.</summary>
[SupportedOSPlatform("windows6.1")]
internal static unsafe class WindowsSystem
{
  /// <summary>A name of this computer in the requested format, or null if Windows can't provide it.</summary>
  public static string? ComputerName(COMPUTER_NAME_FORMAT format)
  {
    uint length = 0;
    PInvoke.GetComputerNameEx(format, default, ref length);
    if (length == 0)
    {
      return null;
    }

    var buffer = new char[length];
    return PInvoke.GetComputerNameEx(format, buffer, ref length)
      ? new string(buffer, 0, (int)length)
      : null;
  }

  /// <summary>Total and in-use physical memory, in bytes.</summary>
  public static (ulong Total, ulong InUse) PhysicalMemory()
  {
    var status = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
    return PInvoke.GlobalMemoryStatusEx(ref status)
      ? (status.ullTotalPhys, status.ullTotalPhys - status.ullAvailPhys)
      : (0, 0);
  }

  /// <summary>
  /// Processor time since boot, summed over all cores (100 ns units). Kernel time already includes idle time.
  /// </summary>
  public static CpuTimes ProcessorTimes()
  {
    FILETIME idle, kernel, user;
    if (!PInvoke.GetSystemTimes(&idle, &kernel, &user))
    {
      return default;
    }

    return new CpuTimes(Ticks(idle), Ticks(kernel) + Ticks(user));
  }

  /// <summary>User names of the sessions that are active right now (console and remote desktop).</summary>
  public static IReadOnlyList<string> ActiveUsers()
  {
    WTS_SESSION_INFOW* sessions = null;
    uint count = 0;
    if (!PInvoke.WTSEnumerateSessions(HANDLE.Null, 0, 1, &sessions, &count))
    {
      return [];
    }

    try
    {
      var users = new List<string>();
      for (var i = 0; i < count; i++)
      {
        if (sessions[i].State == WTS_CONNECTSTATE_CLASS.WTSActive && SessionUser(sessions[i].SessionId) is { } user)
        {
          users.Add(user);
        }
      }

      return users.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    finally
    {
      PInvoke.WTSFreeMemory(sessions);
    }
  }

  private static string? SessionUser(uint sessionId)
  {
    PWSTR name;
    uint bytes;
    if (!PInvoke.WTSQuerySessionInformation(HANDLE.Null, sessionId, WTS_INFO_CLASS.WTSUserName, &name, &bytes))
    {
      return null;
    }

    try
    {
      var value = name.ToString();
      return string.IsNullOrWhiteSpace(value) ? null : value;
    }
    finally
    {
      PInvoke.WTSFreeMemory(name.Value);
    }
  }

  private static ulong Ticks(FILETIME time) => ((ulong)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
}
