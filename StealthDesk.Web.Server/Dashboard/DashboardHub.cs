using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Web.Server.Devices;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.Dashboard;

/// <summary>
/// The realtime endpoint browsers stay connected to. Kept apart from the agent gateway: agents prove who they are
/// with a key, while dashboards are tied to a signed-in user, who counts as online while connected. A dashboard
/// hears only about the devices it subscribed to and may read.
/// </summary>
[Authorize]
[NoPermission("Connecting only marks the user online; each method checks its own permission.")]
public sealed class DashboardHub(
  UserManager<UserRecord> users,
  IDeviceAccess access,
  StealthDeskDb db,
  TimeProvider clock,
  ILogger<DashboardHub> logger)
  : Hub<IDashboardCallbacks>, IDashboardHub
{
  public static string DeviceGroup(Guid deviceId) => $"device:{deviceId}";

  public override async Task OnConnectedAsync()
  {
    await base.OnConnectedAsync();
    await MarkUser(online: true);
  }

  public override async Task OnDisconnectedAsync(Exception? exception)
  {
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
