namespace StealthDesk.Core.Security;

/// <summary>An Ed25519 key pair, both keys as base64 strings.</summary>
public sealed record SigningKeys(string PublicKey, string PrivateKey);
