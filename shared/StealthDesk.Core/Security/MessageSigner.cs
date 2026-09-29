using System.Buffers.Binary;
using System.Text;
using MessagePack;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using StealthDesk.Contracts.Messaging;

namespace StealthDesk.Core.Security;

/// <summary>Signs and checks <see cref="SignedEnvelope{T}"/> messages with Ed25519.</summary>
public interface IMessageSigner
{
  SigningKeys CreateKeys();

  /// <summary>Public key (base64) that belongs to the given private key (base64).</summary>
  string PublicKeyOf(string privateKey);

  SignedEnvelope<T> Sign<T>(T payload, string privateKey);

  /// <summary>True when the signature was made by the owner of <paramref name="publicKey"/>.</summary>
  bool HasValidSignature<T>(SignedEnvelope<T> envelope, string publicKey);

  /// <summary>True when the envelope was signed less than <paramref name="tolerance"/> away from now.</summary>
  bool IsRecent<T>(SignedEnvelope<T> envelope, TimeSpan tolerance);
}

public sealed class MessageSigner(TimeProvider clock) : IMessageSigner
{
  private const int KeyLength = 32;

  // Every signature covers this marker first, so a StealthDesk signature can't be reused for another format.
  private static readonly byte[] _domain = "stealthdesk/signed-envelope/v1"u8.ToArray();

  public SigningKeys CreateKeys()
  {
    var privateKey = new Ed25519PrivateKeyParameters(new SecureRandom());
    var publicKey = privateKey.GeneratePublicKey();
    return new SigningKeys(
      Convert.ToBase64String(publicKey.GetEncoded()),
      Convert.ToBase64String(privateKey.GetEncoded()));
  }

  public string PublicKeyOf(string privateKey)
  {
    var key = new Ed25519PrivateKeyParameters(Convert.FromBase64String(privateKey));
    return Convert.ToBase64String(key.GeneratePublicKey().GetEncoded());
  }

  public SignedEnvelope<T> Sign<T>(T payload, string privateKey)
  {
    var signerKey = PublicKeyOf(privateKey);
    var signedAt = clock.GetUtcNow();

    var signer = new Ed25519Signer();
    signer.Init(forSigning: true, new Ed25519PrivateKeyParameters(Convert.FromBase64String(privateKey)));
    var content = SignedContent(payload, signedAt, signerKey);
    signer.BlockUpdate(content, 0, content.Length);

    return new SignedEnvelope<T>(payload, signedAt, signer.GenerateSignature(), signerKey);
  }

  public bool HasValidSignature<T>(SignedEnvelope<T> envelope, string publicKey)
  {
    if (!TryDecodePublicKey(publicKey, out var keyBytes) || envelope.Signature is not { Length: > 0 })
    {
      return false;
    }

    var verifier = new Ed25519Signer();
    verifier.Init(forSigning: false, new Ed25519PublicKeyParameters(keyBytes));
    var content = SignedContent(envelope.Payload, envelope.SignedAt, envelope.SignerKey);
    verifier.BlockUpdate(content, 0, content.Length);
    return verifier.VerifySignature(envelope.Signature);
  }

  public bool IsRecent<T>(SignedEnvelope<T> envelope, TimeSpan tolerance)
  {
    return (clock.GetUtcNow() - envelope.SignedAt).Duration() <= tolerance;
  }

  /// <summary>True when <paramref name="publicKey"/> is base64 for exactly 32 bytes.</summary>
  public static bool IsWellFormedPublicKey(string? publicKey) => TryDecodePublicKey(publicKey, out _);

  private static bool TryDecodePublicKey(string? publicKey, out byte[] keyBytes)
  {
    keyBytes = [];
    if (string.IsNullOrWhiteSpace(publicKey))
    {
      return false;
    }

    var buffer = new byte[KeyLength];
    if (!Convert.TryFromBase64String(publicKey, buffer, out var written) || written != KeyLength)
    {
      return false;
    }

    keyBytes = buffer;
    return true;
  }

  // domain | signed-at (unix ms, big endian) | signer key length + bytes | payload (MessagePack)
  private static byte[] SignedContent<T>(T payload, DateTimeOffset signedAt, string signerKey)
  {
    var payloadBytes = MessagePackSerializer.Serialize(payload);
    var keyBytes = Encoding.UTF8.GetBytes(signerKey);

    var content = new byte[_domain.Length + sizeof(long) + sizeof(int) + keyBytes.Length + payloadBytes.Length];
    var span = content.AsSpan();

    _domain.CopyTo(span);
    span = span[_domain.Length..];
    BinaryPrimitives.WriteInt64BigEndian(span, signedAt.ToUnixTimeMilliseconds());
    span = span[sizeof(long)..];
    BinaryPrimitives.WriteInt32BigEndian(span, keyBytes.Length);
    span = span[sizeof(int)..];
    keyBytes.CopyTo(span);
    span = span[keyBytes.Length..];
    payloadBytes.CopyTo(span);

    return content;
  }
}
