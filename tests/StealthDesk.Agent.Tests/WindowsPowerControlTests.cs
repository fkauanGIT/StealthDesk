using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.Agent.Windows;
using StealthDesk.Contracts.Devices;

namespace StealthDesk.Agent.Tests;

/// <summary>
/// Never actually launches <c>shutdown.exe</c>: that would restart or shut down whatever machine runs the test.
/// The launch is substituted for one that only records what it was asked to do.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsPowerControlTests
{
  [Theory]
  [InlineData(PowerAction.Restart, "/g /t 0 /f")]
  [InlineData(PowerAction.Shutdown, "/s /t 0 /f")]
  public async Task ChangeState_Launches_ShutdownExe_WithTheRightArguments(PowerAction action, string expectedArguments)
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "shutdown.exe is Windows-only.");

    string? launchedFile = null;
    string? launchedArguments = null;
    var control = new WindowsPowerControl(NullLogger<WindowsPowerControl>.Instance, (file, arguments) =>
    {
      launchedFile = file;
      launchedArguments = arguments;
    });

    var result = await control.ChangeState(action);

    Assert.True(result.Succeeded, result.Error);
    Assert.Equal("shutdown.exe", launchedFile);
    Assert.Equal(expectedArguments, launchedArguments);
  }

  [Fact]
  public async Task ChangeState_Fails_WhenTheLaunchThrows()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "shutdown.exe is Windows-only.");

    var control = new WindowsPowerControl(NullLogger<WindowsPowerControl>.Instance, (_, _) => throw new InvalidOperationException("boom"));

    var result = await control.ChangeState(PowerAction.Restart);

    Assert.False(result.Succeeded);
    Assert.Equal("The device couldn't start the power change.", result.Error);
  }
}
