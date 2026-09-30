using Microsoft.AspNetCore.SignalR;
using StealthDesk.Contracts.Realtime;

namespace StealthDesk.Web.Server.Dashboard;

/// <summary>
/// The realtime endpoint browsers stay connected to. Kept apart from the agent gateway: agents prove who they are
/// with a key, while dashboards will be tied to a signed-in user.
/// </summary>
public sealed class DashboardHub : Hub<IDashboardCallbacks>;
