using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StealthDesk.Agent.Core.DesktopClients;
using StealthDesk.Agent.Core.Settings;
using StealthDesk.Agent.Tests.Fakes;
using StealthDesk.Agent.Windows.DesktopClients;
using StealthDesk.Contracts.DesktopClients;
using StealthDesk.Contracts.Relay;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.DesktopClient;
using StealthDesk.Ipc;

namespace StealthDesk.Agent.Tests;

/// <summary>
/// The agent and a desktop client over a real named pipe. The "desktop client" is this test process: Windows reports
/// it as the process behind the pipe, so the agent takes it only when told this test's executable is the installed one.
/// </summary>
[SupportedOSPlatform("windows6.1")]
public sealed class DesktopClientPipeTests : IAsyncDisposable
{
  private static readonly DesktopClientHello Hello = new(SessionId: 7, Version: "0.7.0");

  private readonly string _pipeName = $"StealthDesk-test-{Guid.NewGuid():N}";
  private readonly List<IDisposable> _disposables = [];
  private static readonly Uri Server = new("https://desk.example.com/");

  private readonly DesktopClientConnections _connections = new(NullLogger<DesktopClientConnections>.Instance);
  private readonly TempAgentFolder _agentFolder = new();
  private readonly TestLifetime _clientLifetime = new();

  public DesktopClientPipeTests()
  {
    Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(6, 1), "Needs Windows named pipes.");
  }

  private static CancellationToken Token => TestContext.Current.CancellationToken;

  [Fact]
  public async Task TheAgentAsks_AndTheClientAnswers()
  {
    var (agentSide, _) = await ConnectAsync(new TestClientEndpoint());

    var answer = await agentSide.Pipe.Ask((client, token) => client.GetSessionInfo(token), cancellationToken: Token);

    Assert.True(answer.Answered, answer.Refusal);
    Assert.Equal(new DesktopSessionInfo(7, "bob", IsConsole: true), answer.Value);
  }

  [Fact]
  public async Task MessagesReachTheOtherEnd_BothWays()
  {
    var endpoint = new TestClientEndpoint();
    var (agentSide, clientSide) = await ConnectAsync(endpoint);

    Assert.Null(await clientSide.Send((agent, token) => agent.ClientReady(Hello, token), cancellationToken: Token));
    Assert.Equal(Hello, agentSide.Hello);

    Assert.Null(await agentSide.Pipe.Send((client, token) => client.Shutdown("because", token), cancellationToken: Token));
    Assert.Equal(["because"], endpoint.Shutdowns);
  }

  [Fact]
  public async Task ARefusedCall_ComesBackWithTheReason()
  {
    var (agentSide, _) = await ConnectAsync(new TestClientEndpoint { Refuse = "the desktop is locked" });

    var answer = await agentSide.Pipe.Ask((client, token) => client.GetSessionInfo(token), cancellationToken: Token);

    Assert.False(answer.Answered);
    Assert.Equal("the desktop is locked", answer.Refusal);
  }

  [Fact]
  public async Task ACallWithNoAnswer_GivesUpAtItsTimeLimit_AndTheOtherEndIsToldToStop()
  {
    var endpoint = new TestClientEndpoint { Hang = true };
    var (agentSide, _) = await ConnectAsync(endpoint);

    var started = DateTime.UtcNow;
    var answer = await agentSide.Pipe.Ask((client, token) => client.GetSessionInfo(token), TimeSpan.FromMilliseconds(300), Token);

    Assert.Equal("No answer within 0.3 s.", answer.Refusal);
    Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(3));
    Assert.True(await Wait.UntilAsync(() => endpoint.Cancelled), "The client wasn't told the call was given up.");
    Assert.True(agentSide.Pipe.IsConnected);
  }

  [Fact]
  public async Task AProcessThatIsntTheInstalledClient_IsRefused_AndItsPipeClosed()
  {
    var host = CreateHost(installedExecutable: @"C:\Program Files\StealthDesk\DesktopClient\StealthDesk.DesktopClient.exe");
    var accepting = host.AcceptOne(Token);
    var stream = await Pipes.ConnectAsync(_pipeName, TimeSpan.FromSeconds(5), Token);
    using var clientSide = Pipes.Open<IAgentEndpoint>(stream, new TestClientEndpoint());

    Assert.False(await accepting);
    Assert.Empty(_connections.All);
    Assert.NotNull(await clientSide.Send((agent, token) => agent.ClientReady(Hello, token), cancellationToken: Token));
    Assert.True(await Wait.UntilAsync(() => !clientSide.IsConnected));
  }

  [Fact]
  public async Task WindowsReportsTheProcessBehindThePipe()
  {
    var pipe = Pipes.CreateServer(_pipeName);
    _disposables.Add(pipe);
    var waiting = pipe.WaitForConnectionAsync(Token);
    using var client = await Pipes.ConnectAsync(_pipeName, TimeSpan.FromSeconds(5), Token);
    await waiting;

    var who = new WindowsPipeClientIdentity().Identify(pipe);

    Assert.True(who.Succeeded, who.Error);
    Assert.Equal(Environment.ProcessId, who.Value!.ProcessId);
    Assert.Equal(Environment.ProcessPath, who.Value.ExecutablePath, ignoreCase: true);
  }

  [Fact]
  public async Task WhenTheClientGoes_TheAgentNotices()
  {
    var (agentSide, clientSide) = await ConnectAsync(new TestClientEndpoint());
    Assert.Single(_connections.All);

    clientSide.Dispose();

    Assert.True(await Wait.UntilAsync(() => _connections.All.Count == 0), "The agent kept a client that is gone.");
    Assert.False(agentSide.Pipe.IsConnected);
    var answer = await agentSide.Pipe.Ask((client, token) => client.GetSessionInfo(token), cancellationToken: Token);
    Assert.Equal("The other end of the pipe is gone.", answer.Refusal);
  }

  [Fact]
  public async Task WhenTheAgentStops_ItTellsItsClientsToClose()
  {
    var endpoint = new TestClientEndpoint();
    await ConnectAsync(endpoint);

    await _connections.CloseAll("The agent is stopping.");

    Assert.Equal(["The agent is stopping."], endpoint.Shutdowns);
    Assert.Empty(_connections.All);
  }

  [Fact]
  public async Task TheClient_SaysWhoItIs_AndReconnectsWhenTheAgentRestarts()
  {
    var host = CreateHost(Environment.ProcessPath!);
    var lifetime = new TestLifetime();
    var client = new AgentConnection(_pipeName, Hello, new TestClientEndpoint(), lifetime, TimeProvider.System, NullLogger<AgentConnection>.Instance);
    await client.StartAsync(Token);
    try
    {
      Assert.True(await host.AcceptOne(Token));
      Assert.True(await Wait.UntilAsync(() => _connections.All.SingleOrDefault()?.Hello == Hello), "The client didn't say who it is.");
      Assert.True(await Wait.UntilAsync(() => client.Current is not null));

      // The agent goes away: its pipes close.
      foreach (var connection in _connections.All)
      {
        connection.Pipe.Dispose();
      }

      Assert.True(await Wait.UntilAsync(() => client.Current is null), "The client didn't notice the agent left.");

      // The agent is back: within the client's retry interval it connects again.
      var accepting = host.AcceptOne(Token);
      Assert.Same(accepting, await Task.WhenAny(accepting, Task.Delay(AgentConnection.RetryEvery + TimeSpan.FromSeconds(5), Token)));
      Assert.True(await accepting);
      Assert.True(await Wait.UntilAsync(() => _connections.All.SingleOrDefault()?.Hello == Hello));
      Assert.False(lifetime.Stopped);
    }
    finally
    {
      await client.StopAsync(CancellationToken.None);
    }
  }

  [Fact]
  public async Task TheClient_GivesUpAfterAMinuteWithoutTheAgent()
  {
    // An agent that refuses every connection: each attempt fails at once.
    using var refusing = new CancellationTokenSource();
    var refuser = Task.Run(async () =>
    {
      try
      {
        while (true)
        {
          await using var pipe = Pipes.CreateServer(_pipeName);
          await pipe.WaitForConnectionAsync(refusing.Token);
        }
      }
      catch (OperationCanceledException)
      {
        // Done.
      }
    }, Token);

    var clock = new FakeTimeProvider();
    var start = clock.GetUtcNow();
    var lifetime = new TestLifetime();
    var client = new AgentConnection(_pipeName, Hello, new TestClientEndpoint(), lifetime, clock, NullLogger<AgentConnection>.Instance);
    await client.StartAsync(Token);
    try
    {
      for (var i = 0; i < 40 && !lifetime.Stopped; i++)
      {
        await Task.Delay(200, Token);
        if (!lifetime.Stopped)
        {
          Assert.True(clock.GetUtcNow() - start < AgentConnection.GiveUpAfter + AgentConnection.RetryEvery, "The client kept trying past a minute.");
          clock.Advance(AgentConnection.RetryEvery);
        }
      }

      Assert.True(lifetime.Stopped, "The client never gave up.");
      Assert.True(client.GaveUp);
      Assert.True(clock.GetUtcNow() - start >= AgentConnection.GiveUpAfter, "The client gave up before a minute.");
    }
    finally
    {
      await refusing.CancelAsync();
      await client.StopAsync(CancellationToken.None);
      await refuser;
    }
  }

  [Fact]
  public void AsTheService_ThePipeLetsInOnlySystemAndInteractiveUsers_AndDeniesAnonymous()
  {
    using var pipe = new WindowsDesktopClientPipeServer(_pipeName, sessionId: 0).Create();

    // Read back from the pipe Windows created, not from the object that asked for it.
    var rules = pipe.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>()
      .Select(x => (Sid: (SecurityIdentifier)x.IdentityReference, x.AccessControlType, x.PipeAccessRights))
      .OrderBy(x => x.Sid.Value)
      .ToList();

    Assert.Equal(
      new[]
      {
        (new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), AccessControlType.Allow, PipeAccessRights.FullControl),
        // .NET adds Synchronize to every rule that allows: a client needs it to wait on the pipe.
        (new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), AccessControlType.Allow, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize),
        (new SecurityIdentifier(WellKnownSidType.AnonymousSid, null), AccessControlType.Deny, PipeAccessRights.FullControl),
      }.OrderBy(x => x.Item1.Value),
      rules);
    var interactive = rules.Single(x => x.Sid.IsWellKnown(WellKnownSidType.InteractiveSid)).PipeAccessRights;
    Assert.False(interactive.HasFlag(PipeAccessRights.CreateNewInstance), "Logged-on users could serve the pipe themselves.");
  }

  [Fact]
  public async Task AsTheService_ALoggedOnUser_ConnectsToThePipe_ButCantServeAnotherInstanceOfIt()
  {
    // This test runs as a logged-on user, not LocalSystem: what the pipe's rules give interactive users applies to it.
    using var pipe = new WindowsDesktopClientPipeServer(_pipeName, sessionId: 0).Create();
    var waiting = pipe.WaitForConnectionAsync(Token);

    using var client = await Pipes.ConnectAsync(_pipeName, TimeSpan.FromSeconds(5), Token);
    await waiting;
    Assert.True(client.IsConnected);

    Assert.Throws<UnauthorizedAccessException>(() => Pipes.CreateServer(_pipeName).Dispose());
  }

  [Fact]
  public void InAUsersSession_ThePipeKeepsTheDefaultAccess()
  {
    using var pipe = new WindowsDesktopClientPipeServer(_pipeName, sessionId: 1).Create();

    var sids = pipe.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>()
      .Select(x => x.IdentityReference)
      .ToList();

    Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), sids);
  }

  [Fact]
  public void ASignedAgent_TakesOnlyProgramsSignedWithItsCertificate()
  {
    // The .NET runtime's own files carry Microsoft's Authenticode signature; this test's executable carries none.
    var signed = typeof(object).Assembly.Location;
    var unsigned = Environment.ProcessPath!;
    Assert.NotNull(WindowsExecutableSigners.Thumbprint(signed));
    Assert.Null(WindowsExecutableSigners.Thumbprint(unsigned));

    Assert.Null(new WindowsExecutableSigners(signed).Mismatch(signed));
    Assert.Equal($"{unsigned} isn't signed, while the agent is.", new WindowsExecutableSigners(signed).Mismatch(unsigned));
    Assert.Null(new WindowsExecutableSigners(unsigned).Mismatch(signed));
  }

  [Fact]
  public void ADebugAgent_AndAnInstalledOne_UseDifferentPipes()
  {
    Assert.Equal("StealthDesk-default", Pipes.NameFor("default", isDebugBuild: false));
    Assert.Equal("StealthDesk-debug-default", Pipes.NameFor("default", isDebugBuild: true));
    Assert.NotEqual(Pipes.NameFor("default", false), Pipes.NameFor("second", false));
  }

  [Fact]
  public async Task TheAgent_HandsARemoteControlSession_ToTheClientOfItsSession_WithTheRelayUnderItsOwnServer()
  {
    var joined = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
    var sessions = new RemoteControlSessions(
      (address, token) =>
      {
        joined.TrySetResult(address);
        return new TaskCompletionSource<System.Net.WebSockets.WebSocket>().Task.WaitAsync(token);
      },
      new SilentNotice(),
      _clientLifetime,
      NullLogger<RemoteControlSessions>.Instance);
    var (_, clientSide) = await ConnectAsync(new DesktopClientEndpoint(sessions, _clientLifetime, NullLogger<DesktopClientEndpoint>.Instance));
    await clientSide.Remote.ClientReady(Hello, Token);
    var request = Request(desktopSession: Hello.SessionId);

    var started = await RemoteControl().StartAsync(request, Token);

    Assert.True(started.Succeeded, started.Error);
    Assert.Equal(request.RelaySessionId, started.Value);
    Assert.Equal(
      RelayConnection.For(Server, request.RelaySessionId, RelayRole.Device, "device-token"),
      await joined.Task.WaitAsync(TimeSpan.FromSeconds(5), Token));
    var session = Assert.Single(sessions.Running);
    Assert.Equal(request.RelaySessionId, session.Id);
    Assert.Equal(request.Permissions, session.Permissions);
  }

  [Fact]
  public async Task NoClientInThatSession_OrOneThatRefuses_IsAnswered_WithWhy()
  {
    var refusing = new TestClientEndpoint { Refuse = "The desktop client is closing." };
    var (_, clientSide) = await ConnectAsync(refusing);
    await clientSide.Remote.ClientReady(Hello, Token);

    var elsewhere = await RemoteControl().StartAsync(Request(desktopSession: 9), Token);
    var refused = await RemoteControl().StartAsync(Request(desktopSession: Hello.SessionId), Token);

    Assert.Equal("No desktop client is running in session 9.", elsewhere.Error);
    Assert.Equal("The desktop client of session 7 didn't start the session: The desktop client is closing.", refused.Error);
  }

  [Fact]
  public async Task TheSessionsShown_AreThoseTheClientsReport()
  {
    await ConnectAsync(new TestClientEndpoint());

    var sessions = await RemoteControl().SessionsAsync(Token);

    Assert.Equal([new RemoteDesktopSession(7, "bob", IsConsole: true)], sessions);
  }

  public ValueTask DisposeAsync()
  {
    foreach (var connection in _connections.All)
    {
      connection.Pipe.Dispose();
    }

    _agentFolder.Dispose();
    _disposables.ForEach(x => x.Dispose());
    return ValueTask.CompletedTask;
  }

  private static RemoteControlRequest Request(int desktopSession) => new(
    Guid.NewGuid(),
    "device-token",
    desktopSession,
    "tech@example.com",
    new ViewerPermissions(Interact: true, UseSecureScreens: false, SendCtrlAltDel: false, ReadClipboard: false, WriteClipboard: false),
    NotifyUser: false);

  private DesktopClientRemoteControl RemoteControl() => new(
    _connections,
    _agentFolder.OpenStore(new AgentSettings { ServerUrl = Server }),
    NullLogger<DesktopClientRemoteControl>.Instance);

  /// <summary>The agent taking this test process as its client, and the client's end.</summary>
  private async Task<(DesktopClientConnection AgentSide, PipeConnection<IAgentEndpoint> ClientSide)> ConnectAsync(IDesktopClientEndpoint endpoint)
  {
    var host = CreateHost(Environment.ProcessPath!);
    var accepting = host.AcceptOne(Token);
    var stream = await Pipes.ConnectAsync(_pipeName, TimeSpan.FromSeconds(5), Token);
    var clientSide = Pipes.Open<IAgentEndpoint>(stream, endpoint);
    _disposables.Add(clientSide);
    Assert.True(await accepting, "The agent refused this test process.");
    return (_connections.All.Single(), clientSide);
  }

  private DesktopClientPipeHost CreateHost(string installedExecutable)
  {
    var authenticator = new DesktopClientAuthenticator(
      new DesktopClientPipeRules(installedExecutable, IsDebugBuild: false),
      new UnsignedAgent(),
      new FakeTimeProvider(),
      NullLogger<DesktopClientAuthenticator>.Instance);
    return new DesktopClientPipeHost(
      new PlainPipeServer(_pipeName),
      new WindowsPipeClientIdentity(),
      authenticator,
      _connections,
      TimeProvider.System,
      NullLogger<DesktopClientPipeHost>.Instance);
  }

  private sealed class PlainPipeServer(string name) : IDesktopClientPipeServer
  {
    public NamedPipeServerStream Create() => Pipes.CreateServer(name);
  }

  private sealed class UnsignedAgent : IExecutableSigners
  {
    public string? Mismatch(string executable) => null;
  }

  private sealed class TestClientEndpoint : IDesktopClientEndpoint
  {
    public string? Refuse { get; init; }

    public bool Hang { get; init; }

    public bool Cancelled { get; private set; }

    public List<string> Shutdowns { get; } = [];

    public async Task<DesktopSessionInfo> GetSessionInfo(CancellationToken cancellationToken)
    {
      if (Refuse is not null)
      {
        throw new InvalidOperationException(Refuse);
      }

      if (Hang)
      {
        try
        {
          await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
          Cancelled = true;
          throw;
        }
      }

      return new DesktopSessionInfo(7, "bob", IsConsole: true);
    }

    public Task Shutdown(string reason, CancellationToken cancellationToken)
    {
      Shutdowns.Add(reason);
      return Task.CompletedTask;
    }

    public List<DesktopRemoteControlStart> RemoteControlStarts { get; } = [];

    public Task StartRemoteControl(DesktopRemoteControlStart request, CancellationToken cancellationToken)
    {
      if (Refuse is not null)
      {
        throw new InvalidOperationException(Refuse);
      }

      RemoteControlStarts.Add(request);
      return Task.CompletedTask;
    }
  }

  private sealed class SilentNotice : IUserNotice
  {
    public void Show(string message)
    {
    }
  }

  private sealed class TestLifetime : IHostApplicationLifetime
  {
    public bool Stopped { get; private set; }

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => CancellationToken.None;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() => Stopped = true;
  }
}
