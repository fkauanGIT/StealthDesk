using System.Management.Automation;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using StealthDesk.Agent.Core.Terminal;
using StealthDesk.Agent.Tests.Fakes;
using StealthDesk.Contracts.Terminal;

namespace StealthDesk.Agent.Tests;

/// <summary>Terminal sessions running real PowerShell inside the test process, as the agent runs them.</summary>
public class TerminalSessionTests
{
  [Fact]
  public async Task Opening_ShowsTheVersionsAndAPromptInTheAgentsFolder()
  {
    await using var terminal = new TerminalHarness();

    await terminal.OpenAsync();

    var folder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    Assert.StartsWith($"PowerShell {PSVersionInfo.PSVersion} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n", terminal.Screen);
    Assert.EndsWith($"PS {folder}> ", terminal.Screen);
  }

  [Fact]
  public async Task OpeningTheSameTerminalAgain_KeepsItsSession()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();
    await terminal.RunAsync("$kept = 'yes'");

    await terminal.Sessions.OpenAsync(terminal.Terminal, TestContext.Current.CancellationToken);

    Assert.Contains("yes", await terminal.RunAsync("$kept"));
    Assert.Single(terminal.Sessions.Running);
  }

  [Fact]
  public async Task Commands_ShowTheirOutput_AndKeepStateBetweenThem()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();

    await terminal.RunAsync("$x = 41");
    var shown = await terminal.RunAsync("$x + 1");
    var table = await terminal.RunAsync("[pscustomobject]@{ Name = 'disk'; Free = 12 }");

    Assert.Equal("42\n", shown);
    // Formatted as the console formats objects: a table with a header.
    Assert.Contains("Name Free", table);
    Assert.Contains("disk   12", table);
  }

  // Programs (whoami, ipconfig, ping) aren't PowerShell commands: unless their output is taken, PowerShell lets them
  // write to the agent's own console, where nobody sees it.
  [Fact]
  public async Task Programs_ShowTheirOutputAndErrorStreamInTheTerminal_AsTheyWriteIt()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();

    var shown = await terminal.RunAsync(Program("echo from-a-program"));
    var errorStream = await terminal.RunAsync(Program("echo to-the-error-stream 1>&2"));
    var before = terminal.Screen.Length;
    terminal.Send(Program(OperatingSystem.IsWindows() ? "echo first& ping -n 4 127.0.0.1 >nul& echo second" : "echo first; sleep 3; echo second"));
    await terminal.ShowsAsync("first", before);
    var whileRunning = terminal.Screen[before..];
    await terminal.ShowsAsync("second", before);

    Assert.Equal("from-a-program\n", shown);
    // As PowerShell's console shows a program's error stream: as its text, not as an error of PowerShell's own.
    Assert.Equal("to-the-error-stream", errorStream.Trim());
    Assert.DoesNotContain("second", whileRunning);
  }

  private static string Program(string line) => OperatingSystem.IsWindows() ? $"cmd /c \"{line}\"" : $"sh -c '{line}'";

  [Fact]
  public async Task Errors_AreSentAsErrorOutput()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();

    await terminal.RunAsync("Get-Item 'no-such-file-here'");
    await terminal.RunAsync("throw 'boom'");
    await terminal.RunAsync("1 +");

    Assert.Contains("no-such-file-here", terminal.Errors);
    Assert.Contains("boom", terminal.Errors);
    Assert.Contains("You must provide a value expression", terminal.Errors);
    Assert.DoesNotContain("no-such-file-here", string.Concat(terminal.Received.Where(x => x.Kind == TerminalOutputKind.Output).Select(x => x.Text)));
  }

  [Fact]
  public async Task WarningsVerboseHostAndProgress_AreShownAsLines()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();

    var shown = await terminal.RunAsync(
      "Write-Warning 'careful'; Write-Verbose 'details' -Verbose; Write-Host 'from host'; Write-Information 'info' -InformationAction Continue; " +
      "1..3 | ForEach-Object { Write-Progress -Activity 'Copying' -Status \"step $_\" -PercentComplete ($_ * 30) }; Write-Progress -Activity 'Copying' -Completed");

    Assert.Contains("WARNING: careful\n", shown);
    Assert.Contains("VERBOSE: details\n", shown);
    Assert.Contains("from host\n", shown);
    Assert.Contains("info\n", shown);
    Assert.Contains("Copying: step 1 (30%)\nCopying: step 2 (60%)\nCopying: step 3 (90%)\n", shown);
  }

  [Fact]
  public async Task CommandsTypedWhileOneRuns_RunAfterIt_InOrder()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();

    terminal.Send("Start-Sleep -Milliseconds 500; 'first'");
    terminal.Send("'second'");
    await terminal.ShowsAsync("second");

    Assert.True(terminal.Screen.IndexOf("first", StringComparison.Ordinal) < terminal.Screen.IndexOf("second", StringComparison.Ordinal));
  }

  [Fact]
  public async Task ReadHost_IsAnsweredWithTheNextLine()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();

    terminal.Send("$name = Read-Host 'Your name'; \"Hello, $name\"");
    await terminal.ShowsAsync("Your name: ");
    var shown = await terminal.RunAsync("Ana");

    Assert.Contains("Hello, Ana", shown);
  }

  [Fact]
  public async Task ASecureReadHost_AndACredential_AreAnswered()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();

    terminal.Send("$secret = Read-Host 'PIN' -AsSecureString; [System.Net.NetworkCredential]::new('', $secret).Password");
    await terminal.ShowsAsync("PIN: ");
    var pin = await terminal.RunAsync("4321");
    terminal.Send("$credential = Get-Credential -UserName 'bob'; $credential.UserName + ':' + $credential.GetNetworkCredential().Password");
    await terminal.ShowsAsync("Password for user bob: ");
    var credential = await terminal.RunAsync("s3cret");

    Assert.Contains("4321", pin);
    Assert.Contains("bob:s3cret", credential);
  }

  [Fact]
  public async Task AChoice_IsAnsweredByItsLetter_AndAskedAgainWhenTheAnswerIsNoChoice()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();

    terminal.Send("$host.UI.PromptForChoice('Fruit', 'Which one?', [System.Management.Automation.Host.ChoiceDescription[]]@('&Apple', '&Banana'), 0)");
    await terminal.ShowsAsync("[A] Apple  [B] Banana  [?] Help (default is \"A\"): ");
    var before = terminal.Screen.Length;
    terminal.Send("cherry");
    await terminal.ShowsAsync("[A] Apple", before);
    var shown = await terminal.RunAsync("b");

    Assert.Contains("Fruit\nWhich one?\n", terminal.Screen);
    Assert.Equal("1\n", shown);
  }

  [Fact]
  public async Task PromptingForFields_AsksForEachOne()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();

    terminal.Send(
      "$fields = [System.Collections.ObjectModel.Collection[System.Management.Automation.Host.FieldDescription]]::new(); " +
      "$fields.Add([System.Management.Automation.Host.FieldDescription]::new('City')); " +
      "$answers = $host.UI.Prompt('Address', 'Where?', $fields); 'City=' + $answers.City");
    await terminal.ShowsAsync("City: ");
    var shown = await terminal.RunAsync("Recife");

    Assert.Contains("City=Recife", shown);
  }

  [Fact]
  public async Task AQuestionNobodyAnswers_StopsTheCommandAfterItsTime()
  {
    var clock = new FakeTimeProvider();
    await using var terminal = new TerminalHarness(clock);
    await terminal.OpenAsync();

    terminal.Send("Read-Host 'Continue'; 'after the question'");
    await terminal.ShowsAsync("Continue: ");
    var before = terminal.Screen.Length;
    clock.Advance(TimeSpan.FromMinutes(4));
    await Task.Delay(200, TestContext.Current.CancellationToken);
    Assert.Equal(before, terminal.Screen.Length);
    clock.Advance(TimeSpan.FromMinutes(1));
    await terminal.UntilAsync(() => terminal.Screen.EndsWith("> ", StringComparison.Ordinal) && terminal.Screen.Length > before);

    Assert.Contains("No answer within 5 minutes", terminal.Errors);
    Assert.DoesNotContain("after the question", terminal.Screen);
    Assert.Contains("still here", await terminal.RunAsync("'still here'"));
  }

  [Fact]
  public async Task Exit_EndsTheSession_WithItsCode()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();

    terminal.Send("exit 3");
    await terminal.UntilAsync(() => terminal.Ended is not null);

    Assert.Equal("The session ended with exit code 3.", terminal.Ended!.Text);
    Assert.Same(terminal.Received[^1], terminal.Ended);
    await terminal.UntilAsync(() => terminal.Sessions.Running.Count == 0, "the session to go");
    Assert.False(terminal.Sessions.Send(terminal.Terminal, "'anyone?'").Succeeded);
  }

  [Fact]
  public async Task AnUnusedSession_IsClosedAfterItsTime_ButNotWhileACommandRuns()
  {
    var clock = new FakeTimeProvider();
    await using var terminal = new TerminalHarness(clock);
    await terminal.OpenAsync();
    terminal.Send("$done = $false; while (-not $done) { Start-Sleep -Milliseconds 50 }; 'loop over'");
    await Task.Delay(300, TestContext.Current.CancellationToken);

    clock.Advance(TimeSpan.FromMinutes(30));
    await Task.Delay(300, TestContext.Current.CancellationToken);
    Assert.Null(terminal.Ended);

    terminal.Sessions.Close(terminal.Terminal);
    await terminal.UntilAsync(() => terminal.Ended is not null);
    Assert.Equal("The session was closed.", terminal.Ended!.Text);

    await using var idle = new TerminalHarness(clock);
    await idle.OpenAsync();
    clock.Advance(TimeSpan.FromMinutes(9));
    await Task.Delay(200, TestContext.Current.CancellationToken);
    Assert.Null(idle.Ended);
    await idle.RunAsync("'used'");
    clock.Advance(TimeSpan.FromMinutes(9));
    await Task.Delay(200, TestContext.Current.CancellationToken);
    Assert.Null(idle.Ended);
    clock.Advance(TimeSpan.FromMinutes(1));
    await idle.UntilAsync(() => idle.Ended is not null);

    Assert.Equal("Closed after 10 minutes without use.", idle.Ended!.Text);
    await idle.UntilAsync(() => idle.Sessions.Running.Count == 0, "the session to go");
  }

  [Fact]
  public async Task Closing_StopsTheCommandRunning()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();
    var marker = Path.Combine(Path.GetTempPath(), $"sd-terminal-{Guid.NewGuid():N}.txt");
    terminal.Send($"Start-Sleep -Seconds 3; Set-Content '{marker}' 'still ran'");
    await Task.Delay(300, TestContext.Current.CancellationToken);

    terminal.Sessions.Close(terminal.Terminal);
    await terminal.UntilAsync(() => terminal.Ended is not null);
    await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);

    Assert.Equal("The session was closed.", terminal.Ended!.Text);
    Assert.Empty(terminal.Sessions.Running);
    Assert.False(File.Exists(marker), "The command went on after the session closed.");
  }

  [Fact]
  public async Task OutputLargerThanOneMessage_ArrivesWhole_InOrder_InSeveralPieces()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();
    var before = terminal.Received.Count;

    var shown = await terminal.RunAsync("1..20000 | ForEach-Object { \"line $_ ação\" }");

    var expected = string.Concat(Enumerable.Range(1, 20000).Select(x => $"line {x} ação\n"));
    Assert.Equal(expected, shown);
    var pieces = terminal.Received.Skip(before).Where(x => x.Kind == TerminalOutputKind.Output).ToList();
    Assert.True(pieces.Count > 3, $"{pieces.Count} pieces");
    Assert.All(pieces, x => Assert.InRange(Encoding.UTF8.GetByteCount(x.Text), 1, TerminalOutput.MaxTextBytes));
  }

  [Fact]
  public void Pieces_NeverCutACharacter_AndKeepEveryByte()
  {
    var text = string.Concat(Enumerable.Repeat("a€😀", 10000));

    var pieces = TerminalOutputBuffer.Pieces(text).ToList();

    Assert.Equal(text, string.Concat(pieces));
    Assert.All(pieces, x =>
    {
      Assert.InRange(Encoding.UTF8.GetByteCount(x), 1, TerminalOutput.MaxTextBytes);
      Assert.False(char.IsHighSurrogate(x[^1]), "A piece ends in half a character.");
    });
  }

  [Fact]
  public async Task OnWindows_BuiltInModulesWork_AndScriptsMustBeRemoteSigned()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows modules and execution policy.");
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();

    var system = await terminal.RunAsync("(Get-CimInstance Win32_OperatingSystem).Caption");
    var owner = await terminal.RunAsync("(Get-Acl $env:WINDIR).Owner");
    var policy = await terminal.RunAsync("Get-ExecutionPolicy");

    Assert.Contains("Windows", system);
    Assert.Contains("TrustedInstaller", owner);
    Assert.Equal("RemoteSigned\n", policy);
    Assert.Empty(terminal.Errors);
  }
}
