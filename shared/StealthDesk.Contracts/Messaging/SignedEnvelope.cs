using MessagePack;

namespace StealthDesk.Contracts.Messaging;

/// <summary>
/// A payload plus the Ed25519 signature that proves who sent it and when.
/// </summary>
/// <param name="Payload">The signed content.</param>
/// <param name="SignedAt">When the sender signed it (UTC). Part of the signature, so it can't be changed.</param>
/// <param name="Signature">Ed25519 signature over payload, time and <paramref name="SignerKey"/>.</param>
/// <param name="SignerKey">The sender's public key (base64). Lets a new device introduce itself.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record SignedEnvelope<T>(T Payload, DateTimeOffset SignedAt, byte[] Signature, string SignerKey);
