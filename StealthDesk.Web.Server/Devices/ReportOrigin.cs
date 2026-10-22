using System.Net;

namespace StealthDesk.Web.Server.Devices;

/// <summary>
/// What the server itself saw about the connection a report arrived on.
/// Never taken from the report: an agent could claim anything.
/// </summary>
public sealed record ReportOrigin(string ConnectionId, IPAddress? RemoteAddress, DateTimeOffset ReceivedAt);
