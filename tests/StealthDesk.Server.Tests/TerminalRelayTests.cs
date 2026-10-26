using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR.Client;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Contracts.Terminal;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>
/// A terminal relayed between a signed-in browser connection and a device's agent: every call checked against
/// <c>device.terminal.use</c> on that device, output delivered only to the connection that opened the session, and
/// sessions closed when that connection goes. The agent is a stand-in that records what reaches it. Needs Docker.
/// </summary>
public class TerminalRelayTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  public static TheoryData<PermissionScopeKind> DenyScopes => [PermissionScopeKind.Device, PermissionScopeKind.DeviceGroup, PermissionScopeKind.Tenant, PermissionScopeKind.Server];

  [Fact]
  public async Task WithThePermission_ASessionOpens_TakesInput_ReturnsOutput_AndCloses()
  {
    await using var world = await World.CreateAsync();

    var opened = await world.OpenAsync();
    Assert.True(opened.Accepted, opened.Error);
    var terminal = opened.Value;
    await world.ShowsAsync(terminal, "PS C:\\> ");

    var sent = await world.InputAsync(terminal, "Get-Date");
    Assert.True(sent.Accepted, sent.Error);
    await world.ShowsAsync(terminal, "ran Get-Date\n");

    var closed = await world.CloseAsync(terminal);
    Assert.True(closed.Accepted, closed.Error);
    await world.ShowsAsync(terminal, "The session was closed.", TerminalOutputKind.Ended);

    Assert.Equal([$"open {terminal}", $"input {terminal} Get-Date", $"close {terminal}"], world.Agent.Calls);
    // Ended, the session is gone: the browser can't use it any more.
    Assert.Equal("The terminal isn't open.", (await world.InputAsync(terminal, "dir")).Error);
  }

  [Fact]
  public async Task WithoutThePermission_EveryCallIsRefused_AndNothingReachesTheAgent()
  {
    await using var world = await World.CreateAsync(canUse: false);

    var opened = await world.OpenAsync();
    var sent = await world.InputAsync(Guid.NewGuid(), "dir");
    var closed = await world.CloseAsync(Guid.NewGuid());

    Assert.Equal("You may not use the terminal on this device.", opened.Error);
    Assert.False(sent.Accepted);
    Assert.False(closed.Accepted);
    Assert.Empty(world.Agent.Calls);
  }

  [Theory]
  [MemberData(nameof(DenyScopes))]
  public async Task Denied_AtAnyScope_EvenAllowedForTheTenant_TheTerminalDoesNotOpen(PermissionScopeKind scope)
  {
    await using var world = await World.CreateAsync();
    await world.AssignAsync(PermissionNames.DeviceTerminalUse, scope, PermissionEffect.Deny);

    var opened = await world.OpenAsync();

    Assert.False(opened.Accepted);
    Assert.Equal("You may not use the terminal on this device.", opened.Error);
    Assert.Empty(world.Agent.Calls);
  }

  [Fact]
  public async Task AnotherTenantsDevice_IsRefusedLikeAMissingOne_EvenHoldingEverythingInYourOwn()
  {
    await using var world = await World.CreateAsync(canUse: false, ownTenant: false);
    await world.AssignAsync(PermissionNames.DeviceRead, PermissionScopeKind.Tenant);
    await world.AssignAsync(PermissionNames.DeviceTerminalUse, PermissionScopeKind.Tenant);

    var opened = await world.OpenAsync();
    var missing = await world.OpenAsync(Guid.NewGuid());

    Assert.Equal("Device not found.", opened.Error);
    Assert.Equal(opened.Error, missing.Error);
    Assert.Empty(world.Agent.Calls);
  }

  [Fact]
  public async Task ADenyAddedDuringASession_RefusesTheNextLine_AndClosesTheSession()
  {
    await using var world = await World.CreateAsync();
    var terminal = (await world.OpenAsync()).Value;
    Assert.True((await world.InputAsync(terminal, "whoami")).Accepted);

    await world.AssignAsync(PermissionNames.DeviceTerminalUse, PermissionScopeKind.Device, PermissionEffect.Deny);
    var sent = await world.InputAsync(terminal, "Remove-Item C:\\data");

    Assert.Equal("You may not use the terminal on this device.", sent.Error);
    Assert.Equal([$"open {terminal}", $"input {terminal} whoami", $"close {terminal}"], world.Agent.Calls);
    Assert.Equal("The terminal isn't open.", (await world.InputAsync(terminal, "dir")).Error);
  }

  [Fact]
  public async Task AnotherBrowser_EvenOfTheSameUser_CannotTypeIn_OrClose_ASessionItDidNotOpen()
  {
    await using var world = await World.CreateAsync();
    var terminal = (await world.OpenAsync()).Value;
    await using var other = await world.ConnectAgainAsync();

    var sent = await other.Connection.InvokeAsync<GatewayReply<Guid>>(nameof(IDashboardHub.SendTerminalInput), terminal, "dir", Cancel);
    var closed = await other.Connection.InvokeAsync<GatewayReply<Guid>>(nameof(IDashboardHub.CloseTerminal), terminal, Cancel);

    Assert.Equal("The terminal isn't open.", sent.Error);
    Assert.Equal("The terminal isn't open.", closed.Error);
    Assert.Equal([$"open {terminal}"], world.Agent.Calls);
  }

  [Fact]
  public async Task Output_ReachesOnlyTheBrowserThatOpenedTheSession()
  {
    await using var world = await World.CreateAsync();
    await using var other = await world.ConnectAgainAsync();
    await other.SubscribeAsync(world.Device);
    var terminal = (await world.OpenAsync()).Value;

    await world.InputAsync(terminal, "hostname");
    await world.ShowsAsync(terminal, "ran hostname\n");
    // A round trip on the other connection: anything sent to it before is in by then.
    await other.SubscribeAsync(world.Device);

    Assert.Empty(other.TerminalOutput);
  }

  [Fact]
  public async Task OutputForAnotherDevicesSession_OrOneTheServerDoesNotKnow_ReachesNoBrowser()
  {
    var logs = new CapturedLogs();
    await using var world = await World.CreateAsync(logs: logs);
    var terminal = (await world.OpenAsync()).Value;
    await world.ShowsAsync(terminal, "PS C:\\> ");
    await using var intruder = await TestAgent.ConnectAsync(world.Server);
    Assert.True((await intruder.ReportAsync(TestAgent.Report(Guid.NewGuid(), "INTRUDER"))).Accepted);
    await using var unreported = await TestAgent.ConnectAsync(world.Server);

    // Each awaited: the server has dealt with it, and anything it delivered is on its way, before the next is sent.
    await intruder.Channel.Server.SendTerminalOutput(new(terminal, TerminalOutputKind.Output, "from another device"));
    await unreported.Channel.Server.SendTerminalOutput(new(terminal, TerminalOutputKind.Output, "from a connection that never reported"));
    await world.Connection.Channel.Server.SendTerminalOutput(new(Guid.NewGuid(), TerminalOutputKind.Output, "for a session nobody opened"));
    await world.Connection.Channel.Server.SendTerminalOutput(new(terminal, TerminalOutputKind.Output, "from the device itself"));
    await world.ShowsAsync(terminal, "from the device itself");

    Assert.Equal(["PS C:\\> ", "from the device itself"], world.Dashboard.TerminalOutput.Select(x => x.Text));
    Assert.Equal(3, logs.Warnings.Count(x => x.Contains("Dropped output of terminal", StringComparison.Ordinal)));
  }

  [Fact]
  public async Task WhenTheBrowserDisconnects_ItsSessionsAreClosedOnTheAgent()
  {
    await using var world = await World.CreateAsync();
    var first = (await world.OpenAsync()).Value;
    var second = (await world.OpenAsync()).Value;

    await world.Dashboard.DisposeAsync();

    Assert.True(
      await Eventually.TrueAsync(() => Task.FromResult(world.Agent.Calls.Count(x => x.StartsWith("close", StringComparison.Ordinal)) == 2)),
      $"The agent got: {string.Join(", ", world.Agent.Calls)}");
    Assert.Contains($"close {first}", world.Agent.Calls);
    Assert.Contains($"close {second}", world.Agent.Calls);
  }

  [Fact]
  public async Task AnOfflineDevice_FailsAtOnce_WithAClearReason()
  {
    await using var world = await World.CreateAsync();
    await world.Connection.DisposeAsync();
    Assert.True(await Eventually.TrueAsync(() => world.Server.WithDbAsync(async db => !(await db.Devices.SingleAsync(x => x.Id == world.Device)).IsOnline)));

    var watch = Stopwatch.StartNew();
    var opened = await world.OpenAsync();

    Assert.Equal("The device is offline.", opened.Error);
    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Took {watch.Elapsed}.");
  }

  [Fact]
  public async Task AnAgentThatDoesNotAnswer_FailsWithinTheTimeLimit_AndIsToldToCloseTheSession()
  {
    await using var world = await World.CreateAsync(callTimeout: "00:00:02");
    world.Agent.HoldOpen = true;

    var opened = await world.OpenAsync();

    Assert.Equal("The device didn't answer within 2 seconds.", opened.Error);
    Assert.Equal(2, world.Agent.Calls.Count);
    Assert.Equal($"close {world.Agent.Calls[0]["open ".Length..]}", world.Agent.Calls[1]);
  }

  [Fact]
  public async Task ClosingASessionTheAgentNoLongerHas_LetsGoOfIt()
  {
    await using var world = await World.CreateAsync();
    var terminal = (await world.OpenAsync()).Value;
    // As after the agent restarted: its sessions are gone without a word.
    world.Agent.Forgetful = true;

    var closed = await world.CloseAsync(terminal);

    Assert.Equal("The terminal session has ended.", closed.Error);
    Assert.Equal("The terminal isn't open.", (await world.InputAsync(terminal, "dir")).Error);
    Assert.Equal([$"open {terminal}", $"close {terminal}"], world.Agent.Calls);
  }

  [Fact]
  public async Task InputLongerThanTheLimit_IsRefused_AndNeverSent()
  {
    await using var world = await World.CreateAsync();
    var terminal = (await world.OpenAsync()).Value;

    var sent = await world.InputAsync(terminal, new string('a', IDashboardHub.MaxTerminalInputLength + 1));
    var fits = await world.InputAsync(terminal, new string('b', IDashboardHub.MaxTerminalInputLength));

    Assert.Equal($"The input is too long ({IDashboardHub.MaxTerminalInputLength + 1} characters); send at most {IDashboardHub.MaxTerminalInputLength} at a time.", sent.Error);
    Assert.True(fits.Accepted, fits.Error);
    Assert.Equal([$"open {terminal}", $"input {terminal} {new string('b', IDashboardHub.MaxTerminalInputLength)}"], world.Agent.Calls);
  }

  /// <summary>
  /// An agent with a pretend terminal: it records every call that reaches it, sends a prompt when a session opens,
  /// echoes each line as output, and ends the session when told to close it.
  /// </summary>
  private sealed class EchoTerminal : RefusingAgent
  {
    private readonly ConcurrentQueue<string> _calls = new();
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TestAgent? Connection { get; set; }

    /// <summary>Takes the call to open and doesn't answer it until the test ends.</summary>
    public bool HoldOpen { get; set; }

    /// <summary>Answers a close as an agent that doesn't have the session.</summary>
    public bool Forgetful { get; set; }

    public IReadOnlyList<string> Calls => [.. _calls];

    public void Release() => _released.TrySetResult();

    public override async Task<GatewayReply<Guid>> OpenTerminal(Guid terminalId, CancellationToken cancellationToken)
    {
      _calls.Enqueue($"open {terminalId}");
      if (HoldOpen)
      {
        await _released.Task;
      }

      Send(new(terminalId, TerminalOutputKind.Output, "PS C:\\> "));
      return GatewayReply.Accept(terminalId);
    }

    public override Task<GatewayReply<Guid>> SendTerminalInput(Guid terminalId, string line, CancellationToken cancellationToken)
    {
      _calls.Enqueue($"input {terminalId} {line}");
      Send(new(terminalId, TerminalOutputKind.Output, $"ran {line}\n"));
      return Task.FromResult(GatewayReply.Accept(terminalId));
    }

    public override Task<GatewayReply<Guid>> CloseTerminal(Guid terminalId, CancellationToken cancellationToken)
    {
      _calls.Enqueue($"close {terminalId}");
      if (Forgetful)
      {
        return Task.FromResult(GatewayReply.Refuse<Guid>("The terminal session has ended."));
      }

      Send(new(terminalId, TerminalOutputKind.Ended, "The session was closed."));
      return Task.FromResult(GatewayReply.Accept(terminalId));
    }

    // Not awaited: a handler waiting on its own connection would hold up the answer it is part of, as the real
    // agent's sessions send from their own loop.
    private void Send(TerminalOutput output) => _ = Connection!.Channel.Server.SendTerminalOutput(output);
  }

  /// <summary>
  /// A device online through the stand-in agent, in a group of its tenant, and a signed-in browser connection of a
  /// user of that tenant or another, who reads the device and uses its terminal unless told otherwise.
  /// </summary>
  private sealed class World : IAsyncDisposable
  {
    private UserRecord _user = null!;
    private string _cookie = null!;
    private Guid _group;

    public ServerHost Server { get; private set; } = null!;
    public EchoTerminal Agent { get; } = new();
    public TestAgent Connection { get; private set; } = null!;
    public TestDashboard Dashboard { get; private set; } = null!;
    public Guid Device { get; private set; }

    public static async Task<World> CreateAsync(bool canUse = true, bool ownTenant = true, string? callTimeout = null, CapturedLogs? logs = null)
    {
      var server = await ServerHost.OnPostgresAsync();
      if (callTimeout is not null)
      {
        server.With("Gateway:AgentCallTimeout", callTimeout);
      }

      if (logs is not null)
      {
        server.WithServices(services => services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs));
      }

      var world = new World { Server = server, Device = Guid.NewGuid(), _group = Guid.NewGuid() };
      world.Connection = await TestAgent.ConnectAsync(server, callbacks: world.Agent);
      world.Agent.Connection = world.Connection;
      var report = await world.Connection.ReportAsync(TestAgent.Report(world.Device));
      Assert.True(report.Accepted, report.Error);

      var tenant = report.Value!.TenantId;
      await server.WithDbAsync(db =>
      {
        db.DeviceGroups.Add(new DeviceGroupRecord { Id = world._group, TenantId = tenant, Name = "Front" });
        db.DeviceGroupMembers.Add(new DeviceGroupMemberRecord { DeviceGroupId = world._group, DeviceId = world.Device });
        return db.SaveChangesAsync();
      });

      var userTenant = ownTenant ? tenant : await TestTenants.CreateAsync(server, "Other tenant");
      world._user = await TestAccounts.CreateUserAsync(server, "tech@example.com", userTenant);
      if (ownTenant)
      {
        await world.AssignAsync(PermissionNames.DeviceRead, PermissionScopeKind.Tenant);
      }

      if (canUse)
      {
        await world.AssignAsync(PermissionNames.DeviceTerminalUse, PermissionScopeKind.Tenant);
      }

      world._cookie = await TestAccounts.SignInForCookieAsync(server, "tech@example.com");
      world.Dashboard = await TestDashboard.ConnectAsync(server, world._cookie);
      return world;
    }

    public Task AssignAsync(string permission, PermissionScopeKind scope, PermissionEffect effect = PermissionEffect.Allow) =>
      TestPermissions.AssignAsync(Server, _user, permission, scope, scope switch
      {
        PermissionScopeKind.Device => Device,
        PermissionScopeKind.DeviceGroup => _group,
        _ => _user.TenantId,
      }, effect);

    /// <summary>A second browser connection of the same user.</summary>
    public Task<TestDashboard> ConnectAgainAsync() => TestDashboard.ConnectAsync(Server, _cookie);

    public Task<GatewayReply<Guid>> OpenAsync(Guid? device = null) =>
      Dashboard.Connection.InvokeAsync<GatewayReply<Guid>>(nameof(IDashboardHub.OpenTerminal), device ?? Device, Cancel);

    public Task<GatewayReply<Guid>> InputAsync(Guid terminal, string line) =>
      Dashboard.Connection.InvokeAsync<GatewayReply<Guid>>(nameof(IDashboardHub.SendTerminalInput), terminal, line, Cancel);

    public Task<GatewayReply<Guid>> CloseAsync(Guid terminal) =>
      Dashboard.Connection.InvokeAsync<GatewayReply<Guid>>(nameof(IDashboardHub.CloseTerminal), terminal, Cancel);

    /// <summary>Waits until the browser received this output of the session.</summary>
    public async Task ShowsAsync(Guid terminal, string text, TerminalOutputKind kind = TerminalOutputKind.Output) =>
      Assert.True(
        await Eventually.TrueAsync(() => Task.FromResult(Dashboard.TerminalOutput.Any(x => x.TerminalId == terminal && x.Kind == kind && x.Text == text))),
        $"Never received \"{text}\"; got: {string.Join(" | ", Dashboard.TerminalOutput.Select(x => $"{x.Kind} {x.Text}"))}");

    public async ValueTask DisposeAsync()
    {
      Agent.Release();
      await Dashboard.DisposeAsync();
      await Connection.DisposeAsync();
      Server.Dispose();
    }
  }
}
