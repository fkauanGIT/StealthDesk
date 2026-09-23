using System.Net;

namespace StealthDesk.Web.Server.Services.DeviceManagement;

/// <summary>
///   This record represents the connection-specific context for a device update.
/// </summary>
/// <param name="ConnectionId">SignalR connection ID.</param>
/// <param name="RemoteIpAddress">The remote IP address of the connecting agent.</param>
/// <param name="LastSeen">The timestamp when the device was last seen online.</param>
/// <param name="IsOnline">Indicates if the device is currently online.</param>
public record DeviceConnectionContext(
    string ConnectionId,
    IPAddress? RemoteIpAddress,
    DateTimeOffset LastSeen,
    bool IsOnline
);
