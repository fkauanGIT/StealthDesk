namespace StealthDesk.Web.Server.KeyProtection;

/// <summary>
/// Encryption at rest of the data protection keys, which sign cookies and protect tokens and email links. Bound from
/// the <c>KeyProtection</c> section. Without it they are stored as plain XML, and anyone who reads the database or a
/// backup of it can forge a sign-in cookie.
/// </summary>
public sealed class KeyProtectionOptions
{
  public const string Section = "KeyProtection";

  /// <summary>Encrypts new keys with <see cref="Certificate"/>. Keys written before stay as they were and still work.</summary>
  public bool EncryptKeys { get; init; }

  /// <summary>The certificate new keys are encrypted with, and that reads them back.</summary>
  public KeyCertificate? Certificate { get; init; }

  /// <summary>
  /// Certificates that only read keys encrypted before a rotation, or before encryption was turned off. Without them,
  /// those keys can't be read and every session and emailed link made with them stops working.
  /// </summary>
  public KeyCertificate[] PreviousCertificates { get; init; } = [];

  public static string Key(string name) => $"{Section}:{name}";
}

/// <summary>A PKCS#12 (.pfx) certificate with an RSA private key, from a file or its base64 contents.</summary>
public sealed class KeyCertificate
{
  /// <summary>Path to the .pfx file. <see cref="Base64"/> wins when both are set.</summary>
  public string? Path { get; init; }

  /// <summary>The .pfx file's contents in base64, for settings that can't carry a file.</summary>
  public string? Base64 { get; init; }

  /// <summary>The .pfx file's password; empty for one without a password.</summary>
  public string? Password { get; init; }

  public bool IsSet => !string.IsNullOrWhiteSpace(Path) || !string.IsNullOrWhiteSpace(Base64);
}
