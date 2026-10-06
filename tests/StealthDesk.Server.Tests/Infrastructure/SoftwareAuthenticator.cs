using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>
/// A passkey authenticator in code, as a phone or Windows Hello would be: it creates a P-256 key pair for the site,
/// answers the server's creation options with a "none" attestation, and signs sign-in challenges. Its JSON is what
/// a browser sends after navigator.credentials.create() and .get().
/// </summary>
public sealed class SoftwareAuthenticator : IDisposable
{
  // User present, user verified (PIN or biometric), and, on creation, attested credential data included.
  private const byte UserPresent = 0x01;
  private const byte UserVerified = 0x04;
  private const byte AttestedCredentialData = 0x40;

  private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
  private uint _signCount;

  public SoftwareAuthenticator(string origin = "http://localhost")
  {
    Origin = origin;
  }

  public string Origin { get; }

  public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);

  public string CredentialIdBase64Url => WebEncoders.Base64UrlEncode(CredentialId);

  /// <summary>The user handle from the creation options, which the authenticator keeps with the key.</summary>
  public byte[] UserHandle { get; private set; } = [];

  /// <summary>Answers creation options from the server, like navigator.credentials.create().</summary>
  public string Create(string creationOptionsJson)
  {
    var options = JsonNode.Parse(creationOptionsJson)!;
    var rpId = options["rp"]!["id"]!.GetValue<string>();
    UserHandle = WebEncoders.Base64UrlDecode(options["user"]!["id"]!.GetValue<string>());
    var clientData = ClientData("webauthn.create", options["challenge"]!.GetValue<string>());

    var authData = AuthenticatorData(rpId, UserPresent | UserVerified | AttestedCredentialData, [.. AttestedCredential()]);
    var attestationObject = Cbor.Map(
      (Cbor.Text("fmt"), Cbor.Text("none")),
      (Cbor.Text("attStmt"), Cbor.Map()),
      (Cbor.Text("authData"), Cbor.Bytes(authData)));

    return Credential(new JsonObject
    {
      ["clientDataJSON"] = WebEncoders.Base64UrlEncode(clientData),
      ["attestationObject"] = WebEncoders.Base64UrlEncode(attestationObject),
      ["transports"] = new JsonArray("internal"),
    });
  }

  /// <summary>Answers request options from the server, like navigator.credentials.get().</summary>
  public string Get(string requestOptionsJson)
  {
    var options = JsonNode.Parse(requestOptionsJson)!;
    var rpId = options["rpId"]!.GetValue<string>();
    var clientData = ClientData("webauthn.get", options["challenge"]!.GetValue<string>());

    var authData = AuthenticatorData(rpId, UserPresent | UserVerified, []);
    var signature = _key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    return Credential(new JsonObject
    {
      ["clientDataJSON"] = WebEncoders.Base64UrlEncode(clientData),
      ["authenticatorData"] = WebEncoders.Base64UrlEncode(authData),
      ["signature"] = WebEncoders.Base64UrlEncode(signature),
      ["userHandle"] = WebEncoders.Base64UrlEncode(UserHandle),
    });
  }

  public void Dispose() => _key.Dispose();

  private string Credential(JsonObject response) => new JsonObject
  {
    ["id"] = CredentialIdBase64Url,
    ["rawId"] = CredentialIdBase64Url,
    ["type"] = "public-key",
    ["authenticatorAttachment"] = "platform",
    ["response"] = response,
    ["clientExtensionResults"] = new JsonObject(),
  }.ToJsonString();

  private byte[] ClientData(string type, string challenge) =>
    JsonSerializer.SerializeToUtf8Bytes(new { type, challenge, origin = Origin, crossOrigin = false });

  private byte[] AuthenticatorData(string rpId, int flags, byte[] extra)
  {
    var counter = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(counter, ++_signCount);
    return [.. SHA256.HashData(Encoding.UTF8.GetBytes(rpId)), (byte)flags, .. counter, .. extra];
  }

  // AAGUID (zeros: no model claimed), the credential ID with its length, and the public key as a COSE key.
  private IEnumerable<byte> AttestedCredential()
  {
    var point = _key.ExportParameters(includePrivateParameters: false).Q;
    var length = new byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)CredentialId.Length);
    var coseKey = Cbor.Map(
      (Cbor.Int(1), Cbor.Int(2)),     // kty: EC2
      (Cbor.Int(3), Cbor.Int(-7)),    // alg: ES256
      (Cbor.Int(-1), Cbor.Int(1)),    // crv: P-256
      (Cbor.Int(-2), Cbor.Bytes(point.X!)),
      (Cbor.Int(-3), Cbor.Bytes(point.Y!)));
    return [.. new byte[16], .. length, .. CredentialId, .. coseKey];
  }

  /// <summary>The few CBOR shapes an attestation needs (RFC 8949).</summary>
  private static class Cbor
  {
    public static byte[] Int(int value) => value >= 0 ? Head(0, (ulong)value) : Head(1, (ulong)(-1 - value));

    public static byte[] Bytes(byte[] value) => [.. Head(2, (ulong)value.Length), .. value];

    public static byte[] Text(string value)
    {
      var bytes = Encoding.UTF8.GetBytes(value);
      return [.. Head(3, (ulong)bytes.Length), .. bytes];
    }

    public static byte[] Map(params (byte[] Key, byte[] Value)[] entries) =>
      [.. Head(5, (ulong)entries.Length), .. entries.SelectMany(x => x.Key.Concat(x.Value))];

    private static byte[] Head(int major, ulong length) => length switch
    {
      < 24 => [(byte)((major << 5) | (int)length)],
      <= byte.MaxValue => [(byte)((major << 5) | 24), (byte)length],
      _ => [(byte)((major << 5) | 25), (byte)(length >> 8), (byte)length],
    };
  }
}
