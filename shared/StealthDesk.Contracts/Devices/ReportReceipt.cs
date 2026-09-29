using MessagePack;

namespace StealthDesk.Contracts.Devices;

/// <summary>
/// The server's answer to an accepted report: the identity it stored the device under.
/// An agent that sees a different <see cref="DeviceId"/> should adopt it.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record ReportReceipt(Guid DeviceId, Guid TenantId, DateTimeOffset ReceivedAt);
