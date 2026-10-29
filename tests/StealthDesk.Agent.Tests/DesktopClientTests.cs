using System.Diagnostics;
using System.Runtime.InteropServices;
using StealthDesk.Branding;
using StealthDesk.Contracts.Agents;
using StealthDesk.DesktopClient;

namespace StealthDesk.Agent.Tests;

/// <summary>The desktop client runs only as the agent starts it: as LocalSystem in a user's session, with its settings.</summary>
public sealed class DesktopClientTests
{
  private static readonly DesktopClientStart Start = new("default", @"C:\ProgramData\StealthDesk\default");

  [Fact]
  public void StartedByTheAgent_AsSystemInAUsersSession_ItRuns()
  {
    Assert.Null(DesktopClientRole.Refusal(isSystem: true, sessionId: 1, Start, isDebugBuild: false));
  }

  [Theory]
  [InlineData(false, 1)]
  [InlineData(true, 0)]
  [InlineData(false, 0)]
  public void StartedAnyOtherWay_ItSaysSo(bool isSystem, int sessionId)
  {
    var refusal = DesktopClientRole.Refusal(isSystem, sessionId, Start, isDebugBuild: false);

    Assert.Equal($"The {Brand.Name} desktop client is started by the {Brand.Name} agent; it can't be opened by hand.", refusal);
  }

  [Fact]
  public void WithoutItsSettings_ItSaysSo_EvenAsSystem()
  {
    Assert.NotNull(DesktopClientRole.Refusal(isSystem: true, sessionId: 1, start: null, isDebugBuild: false));
    Assert.NotNull(DesktopClientRole.Refusal(isSystem: false, sessionId: 1, start: null, isDebugBuild: true));
  }

  [Fact]
  public void ADebugBuild_RunsByHand_ToBeDeveloped()
  {
    Assert.Null(DesktopClientRole.Refusal(isSystem: false, sessionId: 1, Start, isDebugBuild: true));
  }

  [Fact]
  public void TheAgentsArguments_ReadBack_AsTheyWereGiven()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Splits the command line as Windows does.");
    var start = new DesktopClientStart("second-instance", @"C:\Program Data\StealthDesk\second-instance\");
    var arguments = DesktopClientCommandLine.Arguments(start);

    // As Windows splits a command line into arguments.
    var parsed = DesktopClientCommandLine.Parse(CommandLineToArgs(arguments));

    Assert.Equal(start with { DataFolder = @"C:\Program Data\StealthDesk\second-instance" }, parsed);
    Assert.Null(DesktopClientCommandLine.Parse(["--instance", "default"]));
    Assert.Null(DesktopClientCommandLine.Parse([]));
  }

  [Fact]
  public async Task OpenedByHand_TheProgramSaysWhy_AndCloses()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "The desktop client is a Windows program.");
    // Its own build, with everything it needs: the copy next to these tests may lack what only it uses.
    var testBuild = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
    var repository = testBuild.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
    var executable = Path.Combine(
      repository, "StealthDesk.DesktopClient", "bin", testBuild.Parent.Name, testBuild.Name, $"{Brand.DesktopClientName}.exe");
    var start = new ProcessStartInfo(executable) { RedirectStandardError = true, UseShellExecute = false };
    using var process = Process.Start(start)!;

    var said = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
    await process.WaitForExitAsync(TestContext.Current.CancellationToken);

    Assert.True(process.ExitCode == 1, $"Exit code {process.ExitCode}: {said}");
    Assert.Contains("can't be opened by hand", said, StringComparison.Ordinal);
  }

  // Windows' own split of a command line into arguments.
  private static string[] CommandLineToArgs(string arguments)
  {
    var argv = CommandLineToArgvW($"program.exe {arguments}", out var count);
    try
    {
      return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
    }
    finally
    {
      LocalFree(argv);
    }
  }

  [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

  [DllImport("kernel32.dll")]
  private static extern IntPtr LocalFree(IntPtr memory);
}
