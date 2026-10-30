using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.DesktopClients;
using StealthDesk.Ipc;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.RemoteDesktop;

namespace StealthDesk.DesktopClient;

/// <summary>What the desktop client answers to its agent.</summary>
[SupportedOSPlatform("windows6.1")]
internal sealed class DesktopClientEndpoint(IHostApplicationLifetime lifetime, ILogger<DesktopClientEndpoint> logger) : IDesktopClientEndpoint
{
  public Task<DesktopSessionInfo> GetSessionInfo(CancellationToken cancellationToken)
  {
    var sessionId = CurrentSession.Id;
    return Task.FromResult(new DesktopSessionInfo(sessionId, CurrentSession.UserName((uint)sessionId)));
  }

  public Task Shutdown(string reason, CancellationToken cancellationToken)
  {
    logger.LogInformation("The agent asked the desktop client to close: {Reason}", reason);
    lifetime.StopApplication();
    return Task.CompletedTask;
  }
}

[SupportedOSPlatform("windows6.1")]
internal static unsafe class CurrentSession
{
  public static int Id
  {
    get
    {
      using var process = Process.GetCurrentProcess();
      return process.SessionId;
    }
  }

  /// <summary>Who is logged on to <paramref name="sessionId"/>, or null at the sign-in screen.</summary>
  public static string? UserName(uint sessionId)
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
}
