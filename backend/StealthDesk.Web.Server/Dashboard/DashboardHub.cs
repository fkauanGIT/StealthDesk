using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using StealthDesk.Contracts.Realtime;

namespace StealthDesk.Web.Server.Dashboard;

/// <summary>
/// The realtime endpoint browsers stay connected to. Kept apart from the agent gateway: agents prove who they are
/// with a key, while dashboards are tied to a signed-in user, who counts as online while connected.
/// </summary>
public sealed class DashboardHub(UserManager<UserRecord> users, TimeProvider clock, ILogger<DashboardHub> logger)
  : Hub<IDashboardCallbacks>
{
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
