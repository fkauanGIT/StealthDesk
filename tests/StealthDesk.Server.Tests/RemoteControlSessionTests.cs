using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Contracts.Relay;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.Contracts.TenantSettings;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Dashboard;

namespace StealthDesk.Server.Tests;

/// <summary>
/// A browser starting a remote control session of a device: every call checked against
/// <c>device.remote-control.connect</c> on that device, in the user's tenant; the agent told which session to show,
/// as whom, with what the viewer may do and whether to tell the user; both ends meeting on the relay; and the session
/// ended when the browser or the device goes, recorded in the device's access trace. The agent is a stand-in that
/// records what reaches it. Needs Docker.
/// </summary>
public class RemoteControlSessionTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  // A receive that never comes fails the test instead of holding it forever.
  private static CancellationToken Soon => CancellationTokenSource.CreateLinkedTokenSource(Cancel, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).Token;

  public static TheoryData<PermissionScopeKind> DenyScopes => [PermissionScopeKind.Device, PermissionScopeKind.DeviceGroup, PermissionScopeKind.Tenant, PermissionScopeKind.Server];

  [Fact]
  public async Task WithThePermission_ASessionIsIssued_ReachesTheAgent_AndBothEndsMeetOnTheRelay()
  {
    await using var world = await World.CreateAsync();

    var started = await world.StartAsync(desktopSession: 3);

    Assert.True(started.Accepted, started.Error);
    var ticket = started.Value!;
    var request = Assert.Single(world.Agent.Requests);
    Assert.Equal(ticket.RelaySessionId, request.RelaySessionId);
    Assert.Equal(3, request.DesktopSessionId);
    Assert.Equal("tech@example.com", request.ViewerName);
    Assert.NotEqual(ticket.ViewerToken, request.DeviceToken);

    var (viewer, device) = await world.JoinBothAsync(ticket, request);
    await device.SendAsync(Encoding.UTF8.GetBytes("frame"), WebSocketMessageType.Binary, endOfMessage: true, Cancel);
    Assert.Equal("frame", await ReceiveTextAsync(viewer));
  }

  [Fact]
  public async Task TheDevicesSessions_AreAnsweredByItsAgent()
  {
    await using var world = await World.CreateAsync();

    var sessions = await world.SessionsAsync();

    Assert.True(sessions.Accepted, sessions.Error);
    Assert.Equal(DesktopAgent.Sessions, sessions.Value);
  }

  [Fact]
  public async Task TheViewersPermissions_GoWithTheRequest_DecidedWhenItStarts()
  {
    await using var world = await World.CreateAsync();
    await world.StartAsync();
    await world.AssignAsync(PermissionNames.DeviceRemoteControlInteract, PermissionScopeKind.Tenant);
    await world.AssignAsync(PermissionNames.DeviceClipboardRead, PermissionScopeKind.DeviceGroup);
    await world.AssignAsync(PermissionNames.DeviceCtrlAltDelSend, PermissionScopeKind.Device);
    await world.StartAsync();

    Assert.Equal(
      [ViewerPermissions.ViewOnly, new ViewerPermissions(Interact: true, UseSecureScreens: false, SendCtrlAltDel: true, ReadClipboard: true, WriteClipboard: false)],
      world.Agent.Requests.Select(x => x.Permissions));
  }

  [Fact]
  public async Task WithoutThePermission_BothCallsAreRefused_AndNothingReachesTheAgent()
  {
    await using var world = await World.CreateAsync(canSee: false);

    var sessions = await world.SessionsAsync();
    var started = await world.StartAsync();

    Assert.Equal("You may not see this device's desktop.", sessions.Error);
    Assert.Equal("You may not see this device's desktop.", started.Error);
    Assert.Equal(0, world.Agent.SessionCalls);
    Assert.Empty(world.Agent.Requests);
  }

  [Theory]
  [MemberData(nameof(DenyScopes))]
  public async Task Denied_AtAnyScope_EvenAllowedForTheTenant_NoSessionStarts(PermissionScopeKind scope)
  {
    await using var world = await World.CreateAsync();
    await world.AssignAsync(PermissionNames.DeviceRemoteControlConnect, scope, PermissionEffect.Deny);

    var started = await world.StartAsync();

    Assert.Equal("You may not see this device's desktop.", started.Error);
    Assert.Empty(world.Agent.Requests);
  }

  [Fact]
  public async Task AnotherTenantsDevice_IsRefusedLikeAMissingOne_EvenHoldingEverythingInYourOwn()
  {
    await using var world = await World.CreateAsync(canSee: false, ownTenant: false);
    await world.AssignAsync(PermissionNames.DeviceRead, PermissionScopeKind.Tenant);
    await world.AssignAsync(PermissionNames.DeviceRemoteControlConnect, PermissionScopeKind.Tenant);

    var sessions = await world.SessionsAsync();
    var started = await world.StartAsync();
    var missing = await world.StartAsync(device: Guid.NewGuid());

    Assert.Equal("Device not found.", sessions.Error);
    Assert.Equal("Device not found.", started.Error);
    Assert.Equal(started.Error, missing.Error);
    Assert.Equal(0, world.Agent.SessionCalls);
    Assert.Empty(world.Agent.Requests);
  }

  [Theory]
  [InlineData(null, null, true)]
  [InlineData("false", null, false)]
  [InlineData(null, "false", false)]
  [InlineData("true", "false", true)]
  public async Task TheUserIsNotified_AsTheTenantSettingSays_OrTheServersDefault(string? tenantValue, string? serverDefault, bool notified)
  {
    await using var world = await World.CreateAsync(notifyDefault: serverDefault);
    if (tenantValue is not null)
    {
      await world.SetTenantSettingAsync(TenantSettingNames.NotifyUserOnRemoteControl, tenantValue);
    }

    var started = await world.StartAsync();

    Assert.True(started.Accepted, started.Error);
    Assert.Equal(notified, Assert.Single(world.Agent.Requests).NotifyUser);
  }

  [Fact]
  public async Task ADevicesRefusal_ReachesTheBrowser_AndItsRelaySessionIsGone()
  {
    await using var world = await World.CreateAsync();
    world.Agent.Refusal = "No desktop client is running in session 1.";

    var started = await world.StartAsync();

    Assert.Equal("No desktop client is running in session 1.", started.Error);
    var request = Assert.Single(world.Agent.Requests);
    Assert.Equal(404, await RefusedAsync(world.JoinAsync(request.RelaySessionId, RelayRole.Device, request.DeviceToken)));
  }

  [Fact]
  public async Task WhenAnEndLeaves_TheOtherIsClosed_AndTheEndIsTraced()
  {
    using var traced = new TracedActivities();
    await using var world = await World.CreateAsync();
    await world.StartVisitAsync();
    var ticket = (await world.StartAsync(desktopSession: 3)).Value!;
    var (viewer, device) = await world.JoinBothAsync(ticket, Assert.Single(world.Agent.Requests));

    await viewer.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Cancel);
    await ReceiveCloseAsync(device);
    // A visit is traced once it ends.
    await world.EndVisitAsync();

    var activities = await world.TracedAsync(traced, DeviceAccessTracing.RemoteControlEnd);
    var visit = Assert.Single(activities, x => x.OperationName == DeviceAccessTracing.Visit);
    var start = Assert.Single(activities, x => x.OperationName == DeviceAccessTracing.RemoteControlStart);
    Assert.Equal(visit.SpanId, start.ParentSpanId);
    Assert.Equal("accepted", start.GetTagItem(DeviceAccessTracing.OutcomeTag));
    Assert.Equal(3, start.GetTagItem(DeviceAccessTracing.DesktopSessionIdTag));
    var end = Assert.Single(activities, x => x.OperationName == DeviceAccessTracing.RemoteControlEnd);
    Assert.All([start, end], x =>
    {
      Assert.Equal(ticket.RelaySessionId.ToString(), x.GetTagItem(DeviceAccessTracing.RemoteControlSessionIdTag));
      Assert.Equal(world.User.Id.ToString(), x.GetTagItem(DeviceAccessTracing.UserIdTag));
      Assert.Equal("tech@example.com", x.GetTagItem(DeviceAccessTracing.UserNameTag));
    });
    Assert.Equal("An end left the relay.", end.GetTagItem(DeviceAccessTracing.EndReasonTag));
  }

  [Fact]
  public async Task WhenTheBrowserDisconnects_TheSessionEnds_OnBothEnds()
  {
    using var traced = new TracedActivities();
    await using var world = await World.CreateAsync();
    var ticket = (await world.StartAsync()).Value!;
    var (viewer, device) = await world.JoinBothAsync(ticket, Assert.Single(world.Agent.Requests));

    await world.Dashboard.DisposeAsync();

    await ReceiveCloseAsync(device);
    await ReceiveCloseAsync(viewer);
    var end = Assert.Single(await world.TracedAsync(traced, DeviceAccessTracing.RemoteControlEnd), x => x.OperationName == DeviceAccessTracing.RemoteControlEnd);
    Assert.Equal("The browser disconnected.", end.GetTagItem(DeviceAccessTracing.EndReasonTag));
  }

  [Fact]
  public async Task WhenTheDeviceGoesOffline_TheSessionEnds_OnBothEnds()
  {
    using var traced = new TracedActivities();
    await using var world = await World.CreateAsync();
    var ticket = (await world.StartAsync()).Value!;
    var (viewer, device) = await world.JoinBothAsync(ticket, Assert.Single(world.Agent.Requests));

    await world.Connection.DisposeAsync();

    await ReceiveCloseAsync(viewer);
    await ReceiveCloseAsync(device);
    var end = Assert.Single(await world.TracedAsync(traced, DeviceAccessTracing.RemoteControlEnd), x => x.OperationName == DeviceAccessTracing.RemoteControlEnd);
    Assert.Equal("The device went offline.", end.GetTagItem(DeviceAccessTracing.EndReasonTag));
  }

  [Fact]
  public async Task ASessionNobodyJoins_EndsWhenItsTokensExpire()
  {
    using var traced = new TracedActivities();
    await using var world = await World.CreateAsync(ticketLifetime: "00:00:01");
    var ticket = (await world.StartAsync()).Value!;

    var end = Assert.Single(await world.TracedAsync(traced, DeviceAccessTracing.RemoteControlEnd), x => x.OperationName == DeviceAccessTracing.RemoteControlEnd);

    Assert.Equal(ticket.RelaySessionId.ToString(), end.GetTagItem(DeviceAccessTracing.RemoteControlSessionIdTag));
    Assert.Equal(404, await RefusedAsync(world.JoinAsync(ticket.RelaySessionId, RelayRole.Viewer, ticket.ViewerToken)));
  }

  [Fact]
  public async Task ARefusedStart_IsTraced_WithWhy()
  {
    using var traced = new TracedActivities();
    await using var world = await World.CreateAsync(canSee: false);

    await world.StartAsync();

    var start = Assert.Single(await world.TracedAsync(traced, DeviceAccessTracing.RemoteControlStart));
    Assert.Equal("refused", start.GetTagItem(DeviceAccessTracing.OutcomeTag));
    Assert.Equal("You may not see this device's desktop.", start.GetTagItem(DeviceAccessTracing.RefusalTag));
    Assert.Null(start.GetTagItem(DeviceAccessTracing.RemoteControlSessionIdTag));
  }

  // The test server reports a refused handshake as "Incomplete handshake, status code: N".
  private static async Task<int> RefusedAsync(Task<WebSocket> joining)
  {
    var refused = await Assert.ThrowsAnyAsync<Exception>(() => joining);
    var code = refused.Message[(refused.Message.LastIndexOf(' ') + 1)..].TrimEnd('.');
    return int.Parse(code, System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<string> ReceiveTextAsync(WebSocket socket)
  {
    var buffer = new byte[1024];
    var received = await socket.ReceiveAsync(buffer, Soon);
    return Encoding.UTF8.GetString(buffer, 0, received.Count);
  }

  private static async Task ReceiveCloseAsync(WebSocket socket)
  {
    var buffer = new byte[1024];
    while ((await socket.ReceiveAsync(buffer, Soon)).MessageType != WebSocketMessageType.Close)
    {
    }
  }

  /// <summary>An agent with two Windows sessions that records each remote control request and takes it, unless told to refuse.</summary>
  private sealed class DesktopAgent : RefusingAgent
  {
    private readonly ConcurrentQueue<RemoteControlRequest> _requests = new();
    private int _sessionCalls;

    public static IReadOnlyList<RemoteDesktopSession> Sessions { get; } = [new(1, "alice", IsConsole: true), new(3, "bob", IsConsole: false)];

    public string? Refusal { get; set; }

    public IReadOnlyList<RemoteControlRequest> Requests => [.. _requests];

    public int SessionCalls => Volatile.Read(ref _sessionCalls);

    public override Task<GatewayReply<IReadOnlyList<RemoteDesktopSession>>> GetDesktopSessions(CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref _sessionCalls);
      return Task.FromResult(GatewayReply.Accept(Sessions));
    }

    public override Task<GatewayReply<Guid>> StartRemoteControl(RemoteControlRequest request, CancellationToken cancellationToken)
    {
      _requests.Enqueue(request);
      return Task.FromResult(Refusal is null ? GatewayReply.Accept(request.RelaySessionId) : GatewayReply.Refuse<Guid>(Refusal));
    }
  }

  /// <summary>
  /// A device online through the stand-in agent, in a group of its tenant, and a signed-in browser connection of a
  /// user of that tenant or another, who reads the device and may see its desktop unless told otherwise.
  /// </summary>
  private sealed class World : IAsyncDisposable
  {
    private UserRecord _user = null!;
    private string _cookie = null!;
    private Guid _group;
    private Guid _tenant;

    public ServerHost Server { get; private set; } = null!;
    public DesktopAgent Agent { get; } = new();
    public TestAgent Connection { get; private set; } = null!;
    public TestDashboard Dashboard { get; private set; } = null!;
    public Guid Device { get; private set; }

    public UserRecord User => _user;

    public static async Task<World> CreateAsync(
      bool canSee = true, bool ownTenant = true, string? notifyDefault = null, string? ticketLifetime = null)
    {
      var server = await ServerHost.OnPostgresAsync();
      if (notifyDefault is not null)
      {
        server.With("TenantDefaults:NotifyUserOnRemoteControl", notifyDefault);
      }

      if (ticketLifetime is not null)
      {
        server.With("Relay:TicketLifetime", ticketLifetime);
      }

      var world = new World { Server = server, Device = Guid.NewGuid(), _group = Guid.NewGuid() };
      world.Connection = await TestAgent.ConnectAsync(server, callbacks: world.Agent);
      var report = await world.Connection.ReportAsync(TestAgent.Report(world.Device));
      Assert.True(report.Accepted, report.Error);

      world._tenant = report.Value!.TenantId;
      await server.WithDbAsync(db =>
      {
        db.DeviceGroups.Add(new DeviceGroupRecord { Id = world._group, TenantId = world._tenant, Name = "Front" });
        db.DeviceGroupMembers.Add(new DeviceGroupMemberRecord { DeviceGroupId = world._group, DeviceId = world.Device });
        return db.SaveChangesAsync();
      });

      var userTenant = ownTenant ? world._tenant : await TestTenants.CreateAsync(server, "Other tenant");
      world._user = await TestAccounts.CreateUserAsync(server, "tech@example.com", userTenant);
      if (ownTenant)
      {
        await world.AssignAsync(PermissionNames.DeviceRead, PermissionScopeKind.Tenant);
      }

      if (canSee)
      {
        await world.AssignAsync(PermissionNames.DeviceRemoteControlConnect, PermissionScopeKind.Tenant);
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

    public Task SetTenantSettingAsync(string name, string value) =>
      Server.WithDbAsync(db =>
      {
        db.TenantSettings.Add(new TenantSettingRecord { Id = Guid.NewGuid(), TenantId = _tenant, Name = name, Value = value });
        return db.SaveChangesAsync();
      });

    public Task<GatewayReply<IReadOnlyList<RemoteDesktopSession>>> SessionsAsync() =>
      Dashboard.Connection.InvokeAsync<GatewayReply<IReadOnlyList<RemoteDesktopSession>>>(nameof(IDashboardHub.GetDesktopSessions), Device, Cancel);

    public Task<GatewayReply<RemoteControlTicket>> StartAsync(Guid? device = null, int desktopSession = 1) =>
      Dashboard.Connection.InvokeAsync<GatewayReply<RemoteControlTicket>>(nameof(IDashboardHub.StartRemoteControl), device ?? Device, desktopSession, Cancel);

    public Task<GatewayReply<Guid>> StartVisitAsync() =>
      Dashboard.Connection.InvokeAsync<GatewayReply<Guid>>(nameof(IDashboardHub.StartDeviceAccess), Device, Cancel);

    public Task EndVisitAsync() => Dashboard.Connection.InvokeAsync(nameof(IDashboardHub.EndDeviceAccess), Cancel);

    /// <summary>The device's activities, once one named <paramref name="waitFor"/> was recorded.</summary>
    public async Task<IReadOnlyList<System.Diagnostics.Activity>> TracedAsync(TracedActivities traced, string waitFor)
    {
      Assert.True(
        await Eventually.TrueAsync(() => Task.FromResult(traced.Of(Device).Any(x => x.OperationName == waitFor))),
        $"No {waitFor} was traced; got: {string.Join(", ", traced.Of(Device).Select(x => x.OperationName))}");
      return traced.Of(Device);
    }

    public Task<WebSocket> JoinAsync(Guid session, RelayRole role, string token)
    {
      var client = Server.Server.CreateWebSocketClient();
      if (role == RelayRole.Viewer)
      {
        client.ConfigureRequest = request => request.Headers["Cookie"] = _cookie;
      }

      return client.ConnectAsync(RelayConnection.For(Server.Server.BaseAddress, session, role, token), Cancel);
    }

    // Together: neither end is let in before the other arrives.
    public async Task<(WebSocket Viewer, WebSocket Device)> JoinBothAsync(RemoteControlTicket ticket, RemoteControlRequest request)
    {
      var viewer = JoinAsync(ticket.RelaySessionId, RelayRole.Viewer, ticket.ViewerToken);
      var device = JoinAsync(request.RelaySessionId, RelayRole.Device, request.DeviceToken);
      return (await viewer, await device);
    }

    public async ValueTask DisposeAsync()
    {
      await Dashboard.DisposeAsync();
      await Connection.DisposeAsync();
      Server.Dispose();
    }
  }
}
