using MessagePack;

namespace StealthDesk.Contracts.Messaging;

/// <summary>Answer to a call made through a realtime gateway: either a value or the reason it was refused.</summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record GatewayReply<T>(bool Accepted, T? Value, string? Error);

public static class GatewayReply
{
  public static GatewayReply<T> Accept<T>(T value) => new(true, value, null);

  public static GatewayReply<T> Refuse<T>(string error) => new(false, default, error);
}
