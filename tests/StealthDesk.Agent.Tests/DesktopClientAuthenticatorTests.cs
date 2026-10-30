using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StealthDesk.Agent.Core.DesktopClients;

namespace StealthDesk.Agent.Tests;

/// <summary>Who may use the desktop client pipe: the installed client, signed like the agent, and not a program that keeps failing.</summary>
public sealed class DesktopClientAuthenticatorTests
{
  private const string Installed = @"C:\Program Files\StealthDesk\DesktopClient\StealthDesk.DesktopClient.exe";

  private readonly FakeSigners _signers = new();
  private readonly FakeTimeProvider _clock = new();

  [Fact]
  public void TheInstalledClient_SignedLikeTheAgent_IsTaken()
  {
    Assert.Null(Create().Refusal(new PipeClient(10, Installed)));
    Assert.Null(Create().Refusal(new PipeClient(10, Installed.ToUpperInvariant())));
  }

  [Theory]
  [InlineData(@"C:\Users\bob\Downloads\StealthDesk.DesktopClient.exe")]
  [InlineData(@"C:\Windows\System32\cmd.exe")]
  [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
  [InlineData(@"C:\Program Files\StealthDesk\DesktopClient\..\DesktopClient\Other.exe")]
  public void AnyOtherProgram_IsRefused_SayingWhichOneItIs(string path)
  {
    var refusal = Create().Refusal(new PipeClient(10, path));

    Assert.Equal($"{path} isn't the desktop client installed at {Installed}.", refusal);
  }

  [Fact]
  public void TheInstalledPath_SignedByAnotherCertificate_IsRefused()
  {
    _signers.Mismatches[Installed] = "signed with certificate AAAA, not the agent's BBBB.";

    Assert.Equal("signed with certificate AAAA, not the agent's BBBB.", Create().Refusal(new PipeClient(10, Installed)));
  }

  [Fact]
  public void AProgramThatFailedFiveTimesInAMinute_IsRefusedWithoutBeingChecked_UntilTheMinutePasses()
  {
    _signers.Mismatches[Installed] = "not signed";
    var authenticator = Create();
    for (var i = 0; i < DesktopClientAuthenticator.MaxFailuresPerMinute; i++)
    {
      Assert.Equal("not signed", authenticator.Refusal(new PipeClient(10, Installed)));
      _clock.Advance(TimeSpan.FromSeconds(5));
    }

    // Even once it would pass, it is refused without its signature being looked at.
    _signers.Mismatches.Clear();
    var checks = _signers.Checks;
    var refusal = authenticator.Refusal(new PipeClient(10, Installed));

    Assert.Equal($"{Installed} failed 5 times in the last minute; refused without checking.", refusal);
    Assert.Equal(checks, _signers.Checks);

    // A minute after the first failure, one has aged out: it is checked again, and now passes.
    _clock.Advance(TimeSpan.FromSeconds(36));
    Assert.Null(authenticator.Refusal(new PipeClient(10, Installed)));
    Assert.Equal(checks + 1, _signers.Checks);
  }

  [Fact]
  public void FailuresOfOneProgram_DontLimitAnother()
  {
    var authenticator = Create();
    for (var i = 0; i < 10; i++)
    {
      authenticator.Refusal(new PipeClient(10, @"C:\evil.exe"));
    }

    Assert.Null(authenticator.Refusal(new PipeClient(11, Installed)));
  }

  [Fact]
  public void ADebugBuild_AlsoTakesDotnetAndTheClientBuiltInTheSourceTree_WithoutComparingSignatures()
  {
    // Paths of the system the test runs on: the folder rules split paths as that system does.
    var root = Path.Combine(Path.GetTempPath(), "stealthdesk-debug-rules");
    var installed = Path.Combine(root, "agent", "DesktopClient", "StealthDesk.DesktopClient.exe");
    var debugFolder = Path.Combine(root, "src", "StealthDesk.DesktopClient", "bin", "Debug");
    _signers.Mismatches[installed] = "not signed";
    var debug = new DesktopClientAuthenticator(
      new DesktopClientPipeRules(installed, IsDebugBuild: true, debugFolder),
      _signers,
      _clock,
      NullLogger<DesktopClientAuthenticator>.Instance);

    Assert.Null(debug.Refusal(new PipeClient(10, installed)));
    Assert.Null(debug.Refusal(new PipeClient(10, Path.Combine(root, "dotnet", "dotnet.exe"))));
    Assert.Null(debug.Refusal(new PipeClient(10, Path.Combine(debugFolder, "net10.0", "StealthDesk.DesktopClient.exe"))));
    Assert.NotNull(debug.Refusal(new PipeClient(10, Path.Combine(debugFolder, "net10.0", "Other.exe"))));
    Assert.NotNull(debug.Refusal(new PipeClient(10, Path.Combine($"{debugFolder}-copy", "StealthDesk.DesktopClient.exe"))));
    Assert.NotNull(debug.Refusal(new PipeClient(10, Path.Combine(root, "bob", "StealthDesk.DesktopClient.exe"))));
    Assert.Equal(0, _signers.Checks);
  }

  [Fact]
  public void AProcessWithoutAPath_IsRefused()
  {
    Assert.Equal("Process 10 has no executable path.", Create().Refusal(new PipeClient(10, "")));
  }

  private DesktopClientAuthenticator Create() =>
    new(new DesktopClientPipeRules(Installed, IsDebugBuild: false), _signers, _clock, NullLogger<DesktopClientAuthenticator>.Instance);

  private sealed class FakeSigners : IExecutableSigners
  {
    public Dictionary<string, string> Mismatches { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Checks { get; private set; }

    public string? Mismatch(string executable)
    {
      Checks++;
      return Mismatches.GetValueOrDefault(executable);
    }
  }
}
