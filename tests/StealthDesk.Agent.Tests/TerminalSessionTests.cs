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
    Assert.DoesNotContain("\nsecond", whileRunning);
  }

  private static string Program(string line) => OperatingSystem.IsWindows() ? $"cmd /c \"{line}\"" : $"sh -c '{line}'";

  [Fact]
  public async Task Completing_GetChi_GivesGetChildItem_AndTabWalksTheMatchesBothWays()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();

    var first = Complete(terminal, "Get-Chi", forward: true);
    var second = Complete(terminal, "Get-Co", forward: true);
    var third = Complete(terminal, "Get-Co", forward: true);
    var back = Complete(terminal, "Get-Co", forward: false);

    Assert.Equal((0, 7, "Get-ChildItem"), (first.ReplacementIndex, first.ReplacementLength, Assert.Single(first.Matches).CompletionText));
    Assert.Equal(TerminalCompletionKind.Command, first.Matches[0].Kind);
    Assert.NotEqual(second.Matches[0].CompletionText, third.Matches[0].CompletionText);
    Assert.Equal(second.Matches[0].CompletionText, back.Matches[0].CompletionText);
  }

  [Fact]
  public async Task APath_CompletesFromTheDevicesDisk()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();
    var folder = Directory.CreateTempSubdirectory("sd-complete-");
    File.WriteAllText(Path.Combine(folder.FullName, "unique-report-file.txt"), "x");

    try
    {
      var input = $"Get-Content '{folder.FullName}{Path.DirectorySeparatorChar}unique-rep";
      var completed = Complete(terminal, input, forward: true);

      Assert.Contains("unique-report-file.txt", Assert.Single(completed.Matches).CompletionText);
      Assert.Equal(TerminalCompletionKind.ProviderItem, completed.Matches[0].Kind);
    }
    finally
    {
      folder.Delete(recursive: true);
    }
  }

  [Fact]
  public async Task ListingCompletions_GivesEveryPage_AndOnlyThose_AndTooManyAreCounted()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();

    var pages = new List<TerminalCompletions>();
    for (var page = 0; pages.Count == 0 || pages[^1].HasMorePages; page++)
    {
      pages.Add(Complete(terminal, "Get-", forward: null, page));
    }

    var all = pages.SelectMany(x => x.Matches).Select(x => x.CompletionText).ToList();
    var folder = Directory.CreateTempSubdirectory("sd-many-");
    foreach (var i in Enumerable.Range(0, TerminalCompletions.MaxListed + 100))
    {
      File.WriteAllText(Path.Combine(folder.FullName, $"f-{i:000}.txt"), string.Empty);
    }

    TerminalCompletions many;
    try
    {
      many = Complete(terminal, $"Get-Item '{folder.FullName}{Path.DirectorySeparatorChar}f-", forward: null);
    }
    finally
    {
      folder.Delete(recursive: true);
    }

    Assert.True(pages.Count > 1, $"Only {pages.Count} page of Get- commands.");
    Assert.Equal(pages[0].TotalCount, all.Count);
    Assert.Equal(all.Count, all.Distinct().Count());
    Assert.All(pages.SkipLast(1), x => Assert.Equal(TerminalCompletionRequest.PageSize, x.Matches.Count));
    Assert.Equal(TerminalCompletions.MaxListed + 100, many.TotalCount);
    Assert.Empty(many.Matches);
  }

  [Fact]
  public async Task Completing_WhileACommandRuns_IsRefused()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();

    terminal.Send("Start-Sleep -Seconds 2");
    await Task.Delay(300, TestContext.Current.CancellationToken);
    var refused = terminal.Sessions.Complete(terminal.Terminal, new TerminalCompletionRequest("Get-Chi", 7, Forward: true));

    Assert.Equal("Completions aren't available while a command runs.", refused.Error);
  }

  [Fact]
  public async Task Cls_ClearsTheScreen_AndTheNextOutputStartsOnACleanOne()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();
    await terminal.RunAsync("'before'");

    // Not run as the others: what cls clears includes the line itself, shown just before.
    var sent = terminal.Received.Count;
    terminal.Send("cls");
    await terminal.UntilAsync(() => terminal.Received.Skip(sent).Any(x => x.Kind == TerminalOutputKind.Cleared) && terminal.Screen.EndsWith("> ", StringComparison.Ordinal));
    var after = await terminal.RunAsync("'after'");

    var cleared = terminal.Received.ToList().FindLastIndex(x => x.Kind == TerminalOutputKind.Cleared);
    Assert.True(cleared >= 0, "Nothing cleared the screen.");
    var shownSince = string.Concat(terminal.Received.Skip(cleared + 1).Select(x => x.Text));
    Assert.DoesNotContain("before", shownSince);
    Assert.StartsWith("PS ", shownSince);
    Assert.Equal("after\n", after);
  }

  private static TerminalCompletions Complete(TerminalHarness terminal, string input, bool? forward, int page = 0)
  {
    var completed = terminal.Sessions.Complete(terminal.Terminal, new TerminalCompletionRequest(input, input.Length, forward, page));
    Assert.True(completed.Succeeded, completed.Error);
    return completed.Value!;
  }

  [Fact]
  public async Task EachLine_SaysWhetherItAnsweredAQuestion()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();

    var command = terminal.Sessions.Send(terminal.Terminal, "$pin = Read-Host 'PIN' -AsSecureString");
    await terminal.ShowsAsync("PIN: ");
    var answer = terminal.Sessions.Send(terminal.Terminal, "1234");

    Assert.Equal(new TerminalInputReceipt(terminal.Terminal, AnsweredQuestion: false), command.Value);
    Assert.Equal(new TerminalInputReceipt(terminal.Terminal, AnsweredQuestion: true), answer.Value);
  }

  // A browser that leaves while PowerShell loads closes the session before it has its PowerShell.
  [Fact]
  public async Task ClosedWhileItOpens_LetsGoOfItsPowerShell()
  {
    await using var output = new TerminalHarness();
    var modules = PowerShellModules.Prepare(Path.Combine(Path.GetTempPath(), $"sd-modules-{Guid.NewGuid():N}"));
    var session = new TerminalSession(Guid.NewGuid(), output, new TerminalOptions(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, _ => { });

    var starting = session.StartAsync(AppContext.BaseDirectory, modules);
    session.End("The session was closed.");
    await starting;

    Assert.Equal(System.Management.Automation.Runspaces.RunspaceState.Closed, session.PowerShellState);
    await session.DisposeAsync();
  }

  [Fact]
  public async Task WhatIsTyped_IsShownAfterThePrompt_AndAnswersAsTyped()
  {
    await using var terminal = new TerminalHarness();
    await terminal.OpenAsync();

    await terminal.RunAsync("$x = 1");
    terminal.Send("Read-Host 'Name'");
    await terminal.ShowsAsync("Name: ");
    await terminal.AnswerAsync("Ana");

    Assert.Contains("> $x = 1\nPS ", terminal.Screen);
    Assert.Contains("> Read-Host 'Name'\nName: Ana\nAna\nPS ", terminal.Screen);
  }

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
    // Beyond the command shown as typed.
    var output = string.Concat(terminal.Received.Where(x => x.Kind == TerminalOutputKind.Output).Select(x => x.Text));
    Assert.DoesNotContain("no-such-file-here", output.Replace("Get-Item 'no-such-file-here'\n", string.Empty, StringComparison.Ordinal));
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
    await terminal.ShowsAsync("\nsecond\n");

    Assert.True(terminal.Screen.IndexOf("\nfirst\n", StringComparison.Ordinal) is > 0 and var first && first < terminal.Screen.IndexOf("\nsecond\n", StringComparison.Ordinal));
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
    var pin = await terminal.AnswerAsync("4321");
    terminal.Send("$credential = Get-Credential -UserName 'bob'; $credential.UserName + ':' + $credential.GetNetworkCredential().Password");
    await terminal.ShowsAsync("Password for user bob: ");
    var credential = await terminal.AnswerAsync("s3cret");

    // Shown as stars as they are typed; the commands themselves print them back.
    Assert.Equal("****\n4321\n", pin);
    Assert.Equal("******\nbob:s3cret\n", credential);
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
  public async Task AQuestionNobodyAnswers_GoesOnWithAnEmptyAnswerAfterItsTime()
  {
    var clock = new FakeTimeProvider();
    await using var terminal = new TerminalHarness(clock);
    await terminal.OpenAsync();

    terminal.Send("$name = Read-Host 'Continue'; \"answered [$name]\"");
    await terminal.ShowsAsync("Continue: ");
    var before = terminal.Screen.Length;
    clock.Advance(TimeSpan.FromMinutes(4));
    await Task.Delay(200, TestContext.Current.CancellationToken);
    Assert.Equal(before, terminal.Screen.Length);
    clock.Advance(TimeSpan.FromMinutes(1));
    await terminal.UntilAsync(() => terminal.Screen.EndsWith("> ", StringComparison.Ordinal) && terminal.Screen.Length > before);

    Assert.Contains("No answer within 5 minutes: went on as if Enter was pressed.\n", terminal.Screen[before..]);
    Assert.Contains("answered []\n", terminal.Screen[before..]);
    Assert.Empty(terminal.Errors);
    Assert.Contains("still here", await terminal.RunAsync("'still here'"));
  }

  [Theory]
  [InlineData("1", "1")]
  [InlineData("-1", "-1")]
  public async Task AChoiceNobodyAnswers_GetsItsDefault_OrNoChoiceWithoutAskingAgain(string defaultChoice, string expected)
  {
    var clock = new FakeTimeProvider();
    await using var terminal = new TerminalHarness(clock);
    await terminal.OpenAsync();

    terminal.Send($"\"chose [$($host.UI.PromptForChoice('Fruit', 'Which one?', [System.Management.Automation.Host.ChoiceDescription[]]@('&Apple', '&Banana'), {defaultChoice}))]\"");
    await terminal.ShowsAsync("[?] Help");
    var before = terminal.Screen.Length;
    clock.Advance(TimeSpan.FromMinutes(5));
    await terminal.UntilAsync(() => terminal.Screen.EndsWith("> ", StringComparison.Ordinal) && terminal.Screen.Length > before);

    Assert.Contains($"chose [{expected}]\n", terminal.Screen[before..]);
    // Answered once: the question wasn't asked again.
    Assert.DoesNotContain("[?] Help", terminal.Screen[before..]);
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
