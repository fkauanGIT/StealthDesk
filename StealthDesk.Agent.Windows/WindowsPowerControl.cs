using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using StealthDesk.Agent.Core.Power;
using StealthDesk.Contracts.Devices;
using StealthDesk.Core;

namespace StealthDesk.Agent.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPowerControl : IPowerControl
{
  private readonly ILogger<WindowsPowerControl> _logger;
  private readonly Action<string, string> _launch;

  public WindowsPowerControl(ILogger<WindowsPowerControl> logger) : this(logger, LaunchProcess)
  {
  }

  // The real executable is never actually launched by a test: that would restart or shut down the machine running
  // it. Tests use this constructor to substitute a launch that only records what it was asked to do.
  internal WindowsPowerControl(ILogger<WindowsPowerControl> logger, Action<string, string> launch)
  {
    _logger = logger;
    _launch = launch;
  }

  public Task<Outcome<bool>> ChangeState(PowerAction action)
  {
    var arguments = action switch
    {
      PowerAction.Restart => "/g /t 0 /f",
      PowerAction.Shutdown => "/s /t 0 /f",
      _ => null,
    };

    if (arguments is null)
    {
      return Task.FromResult(Outcome.Failure<bool>("Unknown power action."));
    }

    try
    {
      _launch("shutdown.exe", arguments);
      return Task.FromResult(Outcome.Success(true));
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to launch shutdown.exe for {Action}.", action);
      return Task.FromResult(Outcome.Failure<bool>("The device couldn't start the power change."));
    }
  }

  private static void LaunchProcess(string fileName, string arguments) =>
    Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
}
