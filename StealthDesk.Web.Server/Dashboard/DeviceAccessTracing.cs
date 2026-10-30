using System.Diagnostics;
using StealthDesk.Contracts.Messaging;

namespace StealthDesk.Web.Server.Dashboard;

/// <summary>
/// Who used a device and what they did with it, as traces: an activity per visit to a device's remote features, a
/// child for each terminal call and remote control start, and one when a remote control session ends. Commands and
/// answers typed in a terminal are never recorded, only that a line was sent and whether it was taken.
/// </summary>
public static class DeviceAccessTracing
{
  public const string SourceName = "StealthDesk.Web.Server.DeviceAccess";

  public const string Visit = "device.access";
  public const string TerminalOpen = "terminal.open";
  public const string TerminalInput = "terminal.input";
  public const string TerminalCompletions = "terminal.completions";
  public const string TerminalClose = "terminal.close";
  public const string RemoteControlStart = "remote-control.start";

  /// <summary>Not a call: recorded when a remote control session ends, however it ends.</summary>
  public const string RemoteControlEnd = "remote-control.end";

  public const string UserIdTag = "stealthdesk.user.id";
  public const string UserNameTag = "stealthdesk.user.name";
  public const string DeviceIdTag = "stealthdesk.device.id";
  public const string TerminalIdTag = "stealthdesk.terminal.id";
  public const string RemoteControlSessionIdTag = "stealthdesk.remote-control.session.id";

  /// <summary>The Windows session a remote control session was asked for.</summary>
  public const string DesktopSessionIdTag = "stealthdesk.desktop-session.id";

  /// <summary>Why a remote control session ended.</summary>
  public const string EndReasonTag = "stealthdesk.end-reason";

  /// <summary><c>accepted</c> or <c>refused</c>.</summary>
  public const string OutcomeTag = "stealthdesk.outcome";

  /// <summary>Why a call was refused, as the user was told.</summary>
  public const string RefusalTag = "stealthdesk.refusal";

  public static readonly ActivitySource Source = new(SourceName);

  /// <summary>Starts a visit; it lasts until the user leaves the device, or their connection drops.</summary>
  public static Activity? StartVisit(string userId, string? userName, Guid deviceId) =>
    Start(Visit, default, userId, userName, deviceId, terminalId: null);

  /// <summary>Starts a call, a child of <paramref name="visit"/> when the visit is of the same device.</summary>
  public static Activity? StartCall(string name, Activity? visit, string userId, string? userName, Guid? deviceId, Guid? terminalId)
  {
    var parent = visit is not null && deviceId is { } device && Equals(visit.GetTagItem(DeviceIdTag), device.ToString())
      ? visit.Context
      : default;
    return Start(name, parent, userId, userName, deviceId, terminalId);
  }

  /// <summary>Records how a call ended, and answers its reply.</summary>
  public static GatewayReply<T> Record<T>(Activity? call, GatewayReply<T> reply)
  {
    call?.SetTag(OutcomeTag, reply.Accepted ? "accepted" : "refused");
    if (!reply.Accepted)
    {
      call?.SetTag(RefusalTag, reply.Error);
    }

    return reply;
  }

  private static Activity? Start(string name, ActivityContext parent, string userId, string? userName, Guid? deviceId, Guid? terminalId)
  {
    var activity = Source.StartActivity(name, ActivityKind.Internal, parent);
    activity?.SetTag(UserIdTag, userId);
    activity?.SetTag(UserNameTag, userName);
    activity?.SetTag(DeviceIdTag, deviceId?.ToString());
    activity?.SetTag(TerminalIdTag, terminalId?.ToString());
    return activity;
  }
}
