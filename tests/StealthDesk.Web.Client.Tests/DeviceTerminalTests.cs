using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Terminal;
using StealthDesk.Web.Client.Terminal;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The terminal of the device being looked at, without a page: its screen, history and session.</summary>
public class DeviceTerminalTests : BunitContext
{
  private static readonly Guid Device = Guid.NewGuid();
  private readonly FakeDeviceHub _hub = new();
  private readonly DeviceTerminal _terminal;

  public DeviceTerminalTests()
  {
    _terminal = new DeviceTerminal(_hub, Navigation);
    Navigation.NavigateTo($"devices/{Device}/terminal");
  }

  private NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();

  private string Screen => string.Join("\n", _terminal.Lines.Select(x => x.Text));

  [Fact]
  public async Task Opening_ShowsTheBannerAndPrompt_SentBeforeTheAnswer()
  {
    await _terminal.OpenAsync(Device);

    Assert.Equal(TerminalStatus.Open, _terminal.Status);
    Assert.Equal(_hub.NextTerminal, _terminal.TerminalId);
    Assert.Equal("PowerShell 7.6.8 on Windows\nPS C:\\> ", Screen);
  }

  [Fact]
  public async Task OpeningRefused_SaysWhy()
  {
    _hub.OpenReply = _ => GatewayReply.Refuse<Guid>("The device is offline.");

    await _terminal.OpenAsync(Device);

    Assert.Equal(TerminalStatus.Closed, _terminal.Status);
    Assert.Equal("The device is offline.", _terminal.Notice);
  }

  [Fact]
  public async Task Output_OfOtherTerminals_IsLeftOut_AndErrorsKeepTheirKind()
  {
    await _terminal.OpenAsync(Device);

    _hub.Send(Guid.NewGuid(), TerminalOutputKind.Output, "not ours\n");
    _hub.Send(_hub.NextTerminal, TerminalOutputKind.Error, "Get-Item: no such file\n");

    Assert.DoesNotContain("not ours", Screen);
    // The prompt and the error share the prompt's line, each with its own kind.
    Assert.Equal(
      [(TerminalOutputKind.Output, "PS C:\\> "), (TerminalOutputKind.Error, "Get-Item: no such file")],
      _terminal.Lines[1].Spans.Select(x => (x.Kind, x.Text)));
  }

  [Fact]
  public async Task OutputStopsAt500Lines_KeepingTheNewest()
  {
    await _terminal.OpenAsync(Device);

    _hub.Send(_hub.NextTerminal, TerminalOutputKind.Output, string.Concat(Enumerable.Range(1, 700).Select(x => $"line {x}\n")) + "PS C:\\> ");

    Assert.Equal(DeviceTerminal.MaxLines, _terminal.Lines.Count);
    Assert.Equal("line 202", _terminal.Lines[0].Text);
    Assert.Equal("line 700", _terminal.Lines[^2].Text);
    Assert.Equal("PS C:\\> ", _terminal.Lines[^1].Text);
  }

  [Fact]
  public async Task History_WalksBackAndForth_AndKeepsWhatWasBeingTyped()
  {
    await _terminal.OpenAsync(Device);
    foreach (var command in new[] { "Get-Date", "hostname" })
    {
      _terminal.Input = command;
      await _terminal.SubmitAsync();
    }

    _terminal.Input = "Get-Pro";
    _terminal.Previous();
    Assert.Equal("hostname", _terminal.Input);
    _terminal.Previous();
    Assert.Equal("Get-Date", _terminal.Input);
    _terminal.Previous();
    Assert.Equal("Get-Date", _terminal.Input);
    _terminal.Next();
    Assert.Equal("hostname", _terminal.Input);
    _terminal.Next();
    Assert.Equal("Get-Pro", _terminal.Input);
    _terminal.Next();
    Assert.Equal("Get-Pro", _terminal.Input);
  }

  [Fact]
  public async Task History_Keeps500Commands_WithoutBlanksOrRepeats()
  {
    await _terminal.OpenAsync(Device);

    foreach (var command in Enumerable.Range(1, 600).Select(x => $"echo {x}").Concat(["echo 600", "", "   "]))
    {
      _terminal.Input = command;
      await _terminal.SubmitAsync();
    }

    Assert.Equal(DeviceTerminal.MaxHistory, _terminal.History.Count);
    Assert.Equal("echo 101", _terminal.History[0]);
    Assert.Equal("echo 600", _terminal.History[^1]);
    // Every line was sent, the empty ones too: Enter answers a question with its default.
    Assert.Equal(603, _hub.Calls.Count(x => x.StartsWith("input", StringComparison.Ordinal)));
  }

  [Fact]
  public async Task AnswersToQuestions_StayOutOfTheHistory()
  {
    await _terminal.OpenAsync(Device);
    _terminal.Input = "$pin = Read-Host -AsSecureString";
    await _terminal.SubmitAsync();

    _hub.Answering = true;
    _terminal.Input = "1234";
    await _terminal.SubmitAsync();
    _terminal.Previous();

    Assert.Equal(["$pin = Read-Host -AsSecureString"], _terminal.History);
    Assert.Equal("$pin = Read-Host -AsSecureString", _terminal.Input);
  }

  [Fact]
  public async Task ARefusedLine_StaysTyped_WithTheReason()
  {
    await _terminal.OpenAsync(Device);
    _hub.InputReply = GatewayReply.Refuse<TerminalInputReceipt>("You may not use the terminal on this device.");

    _terminal.Input = "Remove-Item C:\\data";
    await _terminal.SubmitAsync();

    Assert.Equal("Remove-Item C:\\data", _terminal.Input);
    Assert.Equal("You may not use the terminal on this device.", _terminal.Notice);
    Assert.Empty(_terminal.History);
  }

  [Fact]
  public async Task Escape_ClearsWhatIsTyped_AndTheDraft()
  {
    await _terminal.OpenAsync(Device);
    _terminal.Input = "dir";
    await _terminal.SubmitAsync();
    _terminal.Input = "half typed";
    _terminal.Previous();

    Assert.True(_terminal.ClearInput());
    _terminal.Previous();
    _terminal.Next();

    Assert.Equal(string.Empty, _terminal.Input);
    Assert.False(_terminal.ClearInput());
  }

  [Fact]
  public async Task Tab_CompletesTheWordAtTheCaret_AndWalksTheMatchesBothWays()
  {
    await _terminal.OpenAsync(Device);
    _terminal.Input = "dir; Get-C";

    await _terminal.CompleteAsync(forward: true, cursor: 10);
    Assert.Equal("dir; Get-ChildItem", _terminal.Input);
    Assert.Equal(18, _terminal.Caret);
    await _terminal.CompleteAsync(forward: true, cursor: 18);
    Assert.Equal("dir; Get-Command", _terminal.Input);
    await _terminal.CompleteAsync(forward: false, cursor: 16);
    Assert.Equal("dir; Get-ChildItem", _terminal.Input);

    // Every request was for what was typed before the first Tab.
    Assert.All(_hub.Calls.Where(x => x.StartsWith("complete", StringComparison.Ordinal)), x => Assert.StartsWith("complete dir; Get-C|10|", x));
  }

  [Fact]
  public async Task TypingAfterTab_CompletesWhatIsTypedNow()
  {
    await _terminal.OpenAsync(Device);
    _terminal.Input = "Get-C";
    await _terminal.CompleteAsync(forward: true, cursor: 5);

    _terminal.Input = "Get-Con";
    await _terminal.CompleteAsync(forward: true, cursor: 7);

    Assert.Equal("Get-Content", _terminal.Input);
  }

  [Fact]
  public async Task NoMatch_OrARefusal_SaysSo_AndLeavesTheInput()
  {
    await _terminal.OpenAsync(Device);
    _terminal.Input = "Zzz";

    await _terminal.CompleteAsync(forward: true, cursor: 3);
    Assert.Equal("No completions.", _terminal.Hint);
    _hub.CompletionReply = GatewayReply.Refuse<TerminalCompletions>("Completions aren't available while a command runs.");
    await _terminal.CompleteAsync(forward: true, cursor: 3);

    Assert.Equal("Completions aren't available while a command runs.", _terminal.Hint);
    Assert.Equal("Zzz", _terminal.Input);
  }

  [Fact]
  public async Task ListingCompletions_GetsEveryPage_AndPickingOneReplacesTheWord()
  {
    await _terminal.OpenAsync(Device);
    _hub.Completable = [.. Enumerable.Range(1, 120).Select(x => $"Item-{x:000}")];
    _terminal.Input = "Get-Item; It";

    await _terminal.ListCompletionsAsync(cursor: 12);

    Assert.Equal(120, _terminal.Completions!.Completions.Matches.Count);
    Assert.Equal(3, _hub.Calls.Count(x => x.StartsWith("complete", StringComparison.Ordinal)));
    _terminal.Choose(_terminal.Completions.Completions.Matches[41]);
    Assert.Equal("Get-Item; Item-042", _terminal.Input);
    Assert.Null(_terminal.Completions);
  }

  [Fact]
  public async Task TooManyCompletions_AreCounted_NotListed()
  {
    await _terminal.OpenAsync(Device);
    _hub.Completable = [.. Enumerable.Range(1, 600).Select(x => $"Item-{x:000}")];
    _terminal.Input = "It";

    await _terminal.ListCompletionsAsync(cursor: 2);

    Assert.Null(_terminal.Completions);
    Assert.Equal("600 matches: type more to narrow them.", _terminal.Hint);
    Assert.Single(_hub.Calls, x => x.StartsWith("complete", StringComparison.Ordinal));
  }

  [Fact]
  public async Task Escape_ClosesTheListFirst_ThenClears()
  {
    await _terminal.OpenAsync(Device);
    _terminal.Input = "Get-C";
    await _terminal.ListCompletionsAsync(cursor: 5);

    Assert.True(_terminal.CloseCompletions());
    Assert.Equal("Get-C", _terminal.Input);
    Assert.False(_terminal.CloseCompletions());
  }

  [Fact]
  public async Task ClearingTheScreen_EmptiesIt_AndTheSessionGoesOn()
  {
    await _terminal.OpenAsync(Device);
    _terminal.Input = "hostname";
    await _terminal.SubmitAsync();

    _hub.Send(_hub.NextTerminal, TerminalOutputKind.Cleared, string.Empty);
    _hub.Send(_hub.NextTerminal, TerminalOutputKind.Output, "PS C:\\> ");

    Assert.Equal("PS C:\\> ", Screen);
    Assert.Equal(TerminalStatus.Open, _terminal.Status);
    Assert.Equal(["hostname"], _terminal.History);
  }

  [Fact]
  public async Task TheSessionEnding_SaysWhy()
  {
    await _terminal.OpenAsync(Device);

    _hub.Send(_hub.NextTerminal, TerminalOutputKind.Ended, "Closed after 10 minutes without use.");

    Assert.Equal(TerminalStatus.Ended, _terminal.Status);
    Assert.Equal("Closed after 10 minutes without use.", _terminal.Notice);
    Assert.Null(_terminal.TerminalId);
  }

  [Fact]
  public async Task TheConnectionDropping_EndsTheSession()
  {
    await _terminal.OpenAsync(Device);

    _hub.Drop();

    Assert.Equal(TerminalStatus.Ended, _terminal.Status);
    Assert.Equal("The connection to the server dropped, which closed the session.", _terminal.Notice);
  }

  [Fact]
  public async Task MovingBetweenTheDevicesPages_KeepsEverything_LeavingTheDeviceClosesTheSession()
  {
    await _terminal.OpenAsync(Device);
    var terminal = _terminal.TerminalId;
    _terminal.Input = "hostname";
    await _terminal.SubmitAsync();

    Navigation.NavigateTo($"devices/{Device}");
    Navigation.NavigateTo($"devices/{Device}/terminal?from=list");

    Assert.Equal(TerminalStatus.Open, _terminal.Status);
    Assert.Equal(["hostname"], _terminal.History);
    Assert.NotEmpty(_terminal.Lines);
    Assert.DoesNotContain($"close {terminal}", _hub.Calls);

    Navigation.NavigateTo("device-groups");

    Assert.Contains($"close {terminal}", _hub.Calls);
    Assert.Equal(TerminalStatus.Closed, _terminal.Status);
    Assert.Null(_terminal.DeviceId);
    Assert.Empty(_terminal.Lines);
    Assert.Empty(_terminal.History);
  }

  [Fact]
  public async Task AVisit_StartsWithTheFirstSession_AndEndsWhenTheUserLeavesTheDevice()
  {
    await _terminal.OpenAsync(Device);
    _hub.Send(_hub.NextTerminal, TerminalOutputKind.Ended, "Closed after 10 minutes without use.");
    await _terminal.OpenAsync(Device);
    Navigation.NavigateTo($"devices/{Device}");
    var whileOnTheDevice = _hub.Calls.ToList();

    Navigation.NavigateTo("device-groups");

    Assert.Equal(1, whileOnTheDevice.Count(x => x == $"visit {Device}"));
    Assert.Equal(0, whileOnTheDevice.Count(x => x == "end visit"));
    Assert.True(whileOnTheDevice.IndexOf($"visit {Device}") < whileOnTheDevice.FindIndex(x => x.StartsWith("open", StringComparison.Ordinal)));
    Assert.Equal("end visit", _hub.Calls.Last());
  }

  [Fact]
  public async Task AfterTheConnectionDropped_TheNextSessionStartsANewVisit()
  {
    await _terminal.OpenAsync(Device);
    _hub.Drop();

    await _terminal.OpenAsync(Device);

    Assert.Equal(2, _hub.Calls.Count(x => x == $"visit {Device}"));
  }

  [Fact]
  public async Task AnotherDevice_GetsItsOwnSession_AndTheFirstOneIsClosed()
  {
    await _terminal.OpenAsync(Device);
    var first = _terminal.TerminalId;
    var other = Guid.NewGuid();
    _hub.NextTerminal = Guid.NewGuid();

    await _terminal.OpenAsync(other);

    Assert.Contains($"close {first}", _hub.Calls);
    Assert.Equal(other, _terminal.DeviceId);
    Assert.Equal(_hub.NextTerminal, _terminal.TerminalId);
    Assert.Equal(2, _terminal.Lines.Count);
  }

  [Fact]
  public async Task LeavingWhileItOpens_ClosesTheSessionOnceItIsThere()
  {
    _hub.HoldOpen = new TaskCompletionSource();
    var opening = _terminal.OpenAsync(Device);

    Navigation.NavigateTo("");
    _hub.HoldOpen.SetResult();
    await opening;

    Assert.Contains($"close {_hub.NextTerminal}", _hub.Calls);
    Assert.Equal(TerminalStatus.Closed, _terminal.Status);
  }
}
