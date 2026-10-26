using MessagePack;

namespace StealthDesk.Contracts.Devices;

/// <summary>
/// What the signed-in user may do on one device they may read, so the web client shows only the actions they can
/// use. The server checks each action again when it is used.
/// </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record DeviceActions(bool UseTerminal);
