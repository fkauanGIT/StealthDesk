using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.Contracts.Terminal;
using StealthDesk.Web.Server.Devices;
using StealthDesk.Web.Server.Gateway;
using StealthDesk.Web.Server.Permissions;
using StealthDesk.Web.Server.Relay;
using StealthDesk.Web.Server.RemoteControl;
using StealthDesk.Web.Server.TenantSettings;
using StealthDesk.Web.Server.Terminal;

namespace StealthDesk.Web.Server.Dashboard;

/// <summary>
/// The realtime endpoint browsers stay connected to. Kept apart from the agent gateway: agents prove who they are
/// with a key, while dashboards are tied to a signed-in user, who counts as online while connected. A dashboard
/// hears only about the devices it subscribed to and may read, and gets the output only of terminals it opened; those
/// terminals and the remote control sessions it started end when it disconnects.
/// </summary>
[Authorize]
[NoPermission("Connecting only marks the user online; each method checks its own permission.")]
public sealed class DashboardHub(
  UserManager<UserRecord> users,
  IDeviceAccess access,
  IAuthorizationService authorization,
  IAgentCalls agents,
  ITerminalRelay terminals,
  IRelaySessions relay,
  IRemoteControlSessions remoteControl,
  TenantSettingService tenantSettings,
  StealthDeskDb db,
  TimeProvider clock,
  ILogger<DashboardHub> logger)
  : Hub<IDashboardCallbacks>, IDashboardHub
{
  // Hub instances live for one call only; the visit outlives it in the connection's items.
  private const string VisitItem = "device-access";

  public static string DeviceGroup(Guid deviceId) => $"device:{deviceId}";

  public override async Task OnConnectedAsync()
  {
    await base.OnConnectedAsync();
    await MarkUser(online: true);
  }

  public override async Task OnDisconnectedAsync(Exception? exception)
  {
    // Nobody is left to see these sessions: closing them stops what they run instead of leaving it to the idle limit.
    foreach (var terminal in terminals.RemoveAll(Context.ConnectionId))
    {
      using var call = StartCall(DeviceAccessTracing.TerminalClose, terminal.DeviceId, terminal.TerminalId);
      call?.SetTag(DeviceAccessTracing.OutcomeTag, "closed: the browser disconnected");
      await CloseOnDeviceAsync(terminal);
    }

    remoteControl.EndForConnection(Context.ConnectionId, "The browser disconnected.");
    EndVisit();
    await MarkUser(online: false);
    await base.OnDisconnectedAsync(exception);
  }

  // Checked once, when subscribing: a grant removed later stops the updates on the dashboard's next connection.
  [ChecksPermission(PermissionNames.DeviceRead)]
  public async Task<GatewayReply<IReadOnlyList<Guid>>> SubscribeToDevices(IReadOnlyList<Guid> deviceIds)
  {
    if (deviceIds.Count > IDashboardHub.MaxDevicesPerSubscription)
    {
      return GatewayReply.Refuse<IReadOnlyList<Guid>>(
        $"Too many devices ({deviceIds.Count}); subscribe to at most {IDashboardHub.MaxDevicesPerSubscription} per call.");
    }

    var requested = deviceIds.Distinct().ToArray();
    var scope = await access.ForAsync(Context.User!, Context.ConnectionAborted);
    var readable = await scope.Apply(db.Devices.AsNoTracking().Where(x => requested.Contains(x.Id)))
      .Select(x => x.Id)
      .ToListAsync(Context.ConnectionAborted);

    foreach (var deviceId in readable)
    {
      await Groups.AddToGroupAsync(Context.ConnectionId, DeviceGroup(deviceId), Context.ConnectionAborted);
    }

    if (readable.Count < requested.Length)
    {
      logger.LogInformation(
        "Connection {ConnectionId} asked for {Refused} of {Requested} devices it may not read or that don't exist.",
        Context.ConnectionId,
        requested.Length - readable.Count,
        requested.Length);
    }

    return GatewayReply.Accept<IReadOnlyList<Guid>>(readable);
  }

  // The actions the web client shows; each one is checked again when used, so this only spares offering what fails.
  [ChecksPermission(PermissionNames.DeviceRead)]
  public async Task<GatewayReply<DeviceActions>> GetDeviceActions(Guid deviceId)
  {
    var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, Context.ConnectionAborted);
    if (device is null || !await MayAsync(device, PermissionNames.DeviceRead))
    {
      return GatewayReply.Refuse<DeviceActions>("Device not found.");
    }

    return GatewayReply.Accept(new DeviceActions(
      UseTerminal: await MayAsync(device, PermissionNames.DeviceTerminalUse),
      SeeDesktop: await MayAsync(device, PermissionNames.DeviceRemoteControlConnect),
      ControlDesktop: await MayAsync(device, PermissionNames.DeviceRemoteControlInteract),
      UseSecureScreens: await MayAsync(device, PermissionNames.DeviceRemoteControlElevatedDesktop),
      SendCtrlAltDel: await MayAsync(device, PermissionNames.DeviceCtrlAltDelSend),
      ReadClipboard: await MayAsync(device, PermissionNames.DeviceClipboardRead),
      WriteClipboard: await MayAsync(device, PermissionNames.DeviceClipboardWrite)));
  }

  /// <summary>Starts recording a visit to the device's remote features, ending any visit of this connection before.</summary>
  [ChecksPermission(PermissionNames.DeviceRead)]
  public async Task<GatewayReply<Guid>> StartDeviceAccess(Guid deviceId)
  {
    var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, Context.ConnectionAborted);
    if (device is null || !await MayAsync(device, PermissionNames.DeviceRead))
    {
      return GatewayReply.Refuse<Guid>("Device not found.");
    }

    EndVisit();
    Context.Items[VisitItem] = DeviceAccessTracing.StartVisit(UserId, Context.User?.Identity?.Name, deviceId);
    return GatewayReply.Accept(deviceId);
  }

  [NoPermission("It only ends the record of a visit this connection started.")]
  public Task EndDeviceAccess()
  {
    EndVisit();
    return Task.CompletedTask;
  }

  [ChecksPermission(PermissionNames.DeviceTerminalUse)]
  public async Task<GatewayReply<Guid>> OpenTerminal(Guid deviceId)
  {
    using var call = StartCall(DeviceAccessTracing.TerminalOpen, deviceId, terminalId: null);
    var opened = DeviceAccessTracing.Record(call, await OpenTerminalCoreAsync(deviceId));
    call?.SetTag(DeviceAccessTracing.TerminalIdTag, opened.Accepted ? opened.Value.ToString() : null);
    return opened;
  }

  // Never traced with what is typed: a line may be a password.
  [ChecksPermission(PermissionNames.DeviceTerminalUse)]
  public async Task<GatewayReply<TerminalInputReceipt>> SendTerminalInput(Guid terminalId, string line)
  {
    using var call = StartCall(DeviceAccessTracing.TerminalInput, DeviceOf(terminalId), terminalId);
    return DeviceAccessTracing.Record(call, await SendTerminalInputCoreAsync(terminalId, line));
  }

  [ChecksPermission(PermissionNames.DeviceTerminalUse)]
  public async Task<GatewayReply<TerminalCompletions>> GetTerminalCompletions(Guid terminalId, TerminalCompletionRequest request)
  {
    using var call = StartCall(DeviceAccessTracing.TerminalCompletions, DeviceOf(terminalId), terminalId);
    return DeviceAccessTracing.Record(call, await GetTerminalCompletionsCoreAsync(terminalId, request));
  }

  [ChecksPermission(PermissionNames.DeviceTerminalUse)]
  public async Task<GatewayReply<Guid>> CloseTerminal(Guid terminalId)
  {
    using var call = StartCall(DeviceAccessTracing.TerminalClose, DeviceOf(terminalId), terminalId);
    return DeviceAccessTracing.Record(call, await CloseTerminalCoreAsync(terminalId));
  }

  [ChecksPermission(PermissionNames.DeviceRemoteControlConnect)]
  public async Task<GatewayReply<IReadOnlyList<RemoteDesktopSession>>> GetDesktopSessions(Guid deviceId)
  {
    var (device, refused) = await RemoteControlDeviceAsync(deviceId);
    if (device is null)
    {
      return GatewayReply.Refuse<IReadOnlyList<RemoteDesktopSession>>(refused!);
    }

    return await agents.CallAsync(device.Id, (agent, token) => agent.GetDesktopSessions(token), Context.ConnectionAborted);
  }

  [ChecksPermission(PermissionNames.DeviceRemoteControlConnect)]
  public async Task<GatewayReply<RemoteControlTicket>> StartRemoteControl(Guid deviceId, int desktopSessionId)
  {
    using var call = StartCall(DeviceAccessTracing.RemoteControlStart, deviceId, terminalId: null);
    call?.SetTag(DeviceAccessTracing.DesktopSessionIdTag, desktopSessionId);
    var started = DeviceAccessTracing.Record(call, await StartRemoteControlCoreAsync(deviceId, desktopSessionId));
    call?.SetTag(DeviceAccessTracing.RemoteControlSessionIdTag, started.Accepted ? started.Value!.RelaySessionId.ToString() : null);
    return started;
  }

  private async Task<GatewayReply<RemoteControlTicket>> StartRemoteControlCoreAsync(Guid deviceId, int desktopSessionId)
  {
    var (device, refused) = await RemoteControlDeviceAsync(deviceId);
    if (device is null)
    {
      return GatewayReply.Refuse<RemoteControlTicket>(refused!);
    }

    // Decided now, for the whole session: the desktop client holds the viewer to them.
    var permissions = new ViewerPermissions(
      Interact: await MayAsync(device, PermissionNames.DeviceRemoteControlInteract),
      UseSecureScreens: await MayAsync(device, PermissionNames.DeviceRemoteControlElevatedDesktop),
      SendCtrlAltDel: await MayAsync(device, PermissionNames.DeviceCtrlAltDelSend),
      ReadClipboard: await MayAsync(device, PermissionNames.DeviceClipboardRead),
      WriteClipboard: await MayAsync(device, PermissionNames.DeviceClipboardWrite));
    var notifyUser = await tenantSettings.NotifyUserOnRemoteControlAsync(device.TenantId, Context.ConnectionAborted);
    var viewerName = Context.User?.Identity?.Name ?? "A technician";

    var tickets = relay.Issue(Guid.Parse(UserId));
    var request = new RemoteControlRequest(tickets.SessionId, tickets.DeviceToken, desktopSessionId, viewerName, permissions, notifyUser);
    GatewayReply<Guid> started;
    try
    {
      started = await agents.CallAsync(device.Id, (agent, token) => agent.StartRemoteControl(request, token), Context.ConnectionAborted);
    }
    catch (OperationCanceledException)
    {
      started = GatewayReply.Refuse<Guid>("The connection closed.");
    }

    if (!started.Accepted)
    {
      // The device may have taken it after the server stopped waiting: its end finds the session gone.
      relay.End(tickets.SessionId);
      return GatewayReply.Refuse<RemoteControlTicket>(started.Error ?? "The remote control session couldn't start.");
    }

    remoteControl.Add(new RemoteControlLink(tickets.SessionId, device.Id, Context.ConnectionId, UserId, Context.User?.Identity?.Name));
    if (Context.ConnectionAborted.IsCancellationRequested)
    {
      // Gone while the device answered: its disconnect ended this connection's sessions before this one was added.
      remoteControl.EndForConnection(Context.ConnectionId, "The browser disconnected.");
    }

    return GatewayReply.Accept(new RemoteControlTicket(tickets.SessionId, tickets.ViewerToken));
  }

  // Checked on every call; a device the user may not read is refused as one that doesn't exist.
  private async Task<(DeviceRecord? Device, string? Refused)> RemoteControlDeviceAsync(Guid deviceId)
  {
    var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, Context.ConnectionAborted);
    if (device is not null && await MayAsync(device, PermissionNames.DeviceRemoteControlConnect))
    {
      return (device, null);
    }

    return (null, device is not null && await MayAsync(device, PermissionNames.DeviceRead)
      ? "You may not see this device's desktop."
      : "Device not found.");
  }

  private async Task<GatewayReply<Guid>> OpenTerminalCoreAsync(Guid deviceId)
  {
    if (await RefuseTerminalAsync(deviceId) is { } refused)
    {
      return GatewayReply.Refuse<Guid>(refused);
    }

    // The server names the session, so no browser can pick the id of one another browser opened.
    var terminal = new TerminalLink(Guid.NewGuid(), deviceId, Context.ConnectionId);
    // Before the call: the agent sends the banner and first prompt before it answers.
    terminals.Add(terminal);
    GatewayReply<Guid> opened;
    try
    {
      opened = await agents.CallAsync(deviceId, (agent, token) => agent.OpenTerminal(terminal.TerminalId, token), Context.ConnectionAborted);
    }
    catch (OperationCanceledException)
    {
      opened = GatewayReply.Refuse<Guid>("The connection closed.");
    }

    if (opened.Accepted)
    {
      logger.LogInformation("Terminal {TerminalId} opened on device {DeviceId} by connection {ConnectionId}.", terminal.TerminalId, deviceId, Context.ConnectionId);
      return GatewayReply.Accept(terminal.TerminalId);
    }

    // The agent may have started it after the server stopped waiting.
    terminals.Remove(terminal.TerminalId);
    await CloseOnDeviceAsync(terminal);
    return GatewayReply.Refuse<Guid>(opened.Error ?? "The terminal couldn't open.");
  }

  private async Task<GatewayReply<TerminalInputReceipt>> SendTerminalInputCoreAsync(Guid terminalId, string line)
  {
    if (line.Length > IDashboardHub.MaxTerminalInputLength)
    {
      return GatewayReply.Refuse<TerminalInputReceipt>(
        $"The input is too long ({line.Length} characters); send at most {IDashboardHub.MaxTerminalInputLength} at a time.");
    }

    var (terminal, refused) = await TerminalOfAsync(terminalId);
    if (terminal is null)
    {
      return GatewayReply.Refuse<TerminalInputReceipt>(refused!);
    }

    return await agents.CallAsync(terminal.DeviceId, (agent, token) => agent.SendTerminalInput(terminalId, line, token), Context.ConnectionAborted);
  }

  private async Task<GatewayReply<TerminalCompletions>> GetTerminalCompletionsCoreAsync(Guid terminalId, TerminalCompletionRequest request)
  {
    if (request.Input.Length > IDashboardHub.MaxTerminalInputLength || request.Page < 0)
    {
      return GatewayReply.Refuse<TerminalCompletions>("The completion request is out of range.");
    }

    var (terminal, refused) = await TerminalOfAsync(terminalId);
    if (terminal is null)
    {
      return GatewayReply.Refuse<TerminalCompletions>(refused!);
    }

    return await agents.CallAsync(terminal.DeviceId, (agent, token) => agent.GetTerminalCompletions(terminalId, request, token), Context.ConnectionAborted);
  }

  private async Task<GatewayReply<Guid>> CloseTerminalCoreAsync(Guid terminalId)
  {
    var (terminal, refused) = await TerminalOfAsync(terminalId);
    if (terminal is null)
    {
      return GatewayReply.Refuse<Guid>(refused!);
    }

    // Kept until the agent's last output, which tells the browser the session ended; a device that can't say so
    // never will.
    var closed = await agents.CallAsync(terminal.DeviceId, (agent, token) => agent.CloseTerminal(terminalId, token), Context.ConnectionAborted);
    if (!closed.Accepted)
    {
      terminals.Remove(terminalId);
    }

    return closed;
  }

  private string UserId => Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

  private Guid? DeviceOf(Guid terminalId) => terminals.Find(terminalId, Context.ConnectionId)?.DeviceId;

  private Activity? StartCall(string name, Guid? deviceId, Guid? terminalId) =>
    DeviceAccessTracing.StartCall(
      name, Context.Items.TryGetValue(VisitItem, out var visit) ? visit as Activity : null, UserId, Context.User?.Identity?.Name, deviceId, terminalId);

  private void EndVisit()
  {
    if (Context.Items.Remove(VisitItem, out var visit))
    {
      (visit as Activity)?.Dispose();
    }
  }

  // Checked on every call: a deny added or a grant removed applies to the next line typed, not the next session.
  private async Task<string?> RefuseTerminalAsync(Guid deviceId)
  {
    var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, Context.ConnectionAborted);
    if (device is not null && await MayAsync(device, PermissionNames.DeviceTerminalUse))
    {
      return null;
    }

    return device is not null && await MayAsync(device, PermissionNames.DeviceRead)
      ? "You may not use the terminal on this device."
      : "Device not found.";
  }

  // A session this connection opened, still allowed. Whoever loses the permission loses the session with it.
  private async Task<(TerminalLink? Terminal, string? Refused)> TerminalOfAsync(Guid terminalId)
  {
    if (terminals.Find(terminalId, Context.ConnectionId) is not { } terminal)
    {
      return (null, "The terminal isn't open.");
    }

    if (await RefuseTerminalAsync(terminal.DeviceId) is { } refused)
    {
      terminals.Remove(terminalId);
      await CloseOnDeviceAsync(terminal);
      return (null, refused);
    }

    return (terminal, null);
  }

  // Not tied to the connection: it may be closing already, and the session must close all the same.
  private async Task CloseOnDeviceAsync(TerminalLink terminal)
  {
    try
    {
      var closed = await agents.CallAsync(terminal.DeviceId, (agent, token) => agent.CloseTerminal(terminal.TerminalId, token));
      if (!closed.Accepted)
      {
        logger.LogDebug("Terminal {TerminalId} wasn't closed on device {DeviceId}: {Error}", terminal.TerminalId, terminal.DeviceId, closed.Error);
      }
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to close terminal {TerminalId} on device {DeviceId}.", terminal.TerminalId, terminal.DeviceId);
    }
  }

  private async Task<bool> MayAsync(DeviceRecord device, string permission) =>
    (await authorization.AuthorizeAsync(Context.User!, device, PermissionPolicies.For(permission))).Succeeded;

  [NoPermission("It only stops updates the connection already receives.")]
  public async Task UnsubscribeFromDevices(IReadOnlyList<Guid> deviceIds)
  {
    foreach (var deviceId in deviceIds.Distinct())
    {
      await Groups.RemoveFromGroupAsync(Context.ConnectionId, DeviceGroup(deviceId), Context.ConnectionAborted);
    }
  }

  // A presence flag must never break the connection it describes.
  private async Task MarkUser(bool online)
  {
    try
    {
      if (Context.User is null || await users.GetUserAsync(Context.User) is not { } user)
      {
        return;
      }

      user.IsOnline = online;
      if (online)
      {
        user.LastSignIn = clock.GetUtcNow();
      }

      await users.UpdateAsync(user);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to mark the user of connection {ConnectionId} {State}.", Context.ConnectionId, online ? "online" : "offline");
    }
  }
}
