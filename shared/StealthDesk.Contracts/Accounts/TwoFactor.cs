namespace StealthDesk.Contracts.Accounts;

/// <summary>Where the signed-in user stands with two-factor authentication.</summary>
public sealed record TwoFactorStatus
{
  public bool Enabled { get; init; }

  /// <summary>An authenticator key exists, from a setup that may not have been finished.</summary>
  public bool HasAuthenticator { get; init; }

  public int RecoveryCodesLeft { get; init; }

  /// <summary>This browser skips the code when signing in.</summary>
  public bool IsBrowserRemembered { get; init; }
}

/// <summary>What the authenticator app needs: the key typed by hand, or the same key in a QR code.</summary>
public sealed record AuthenticatorSetup
{
  /// <summary>In groups of four, easier to type.</summary>
  public string SharedKey { get; init; } = string.Empty;

  /// <summary>The otpauth:// address the QR code holds.</summary>
  public string AuthenticatorUri { get; init; } = string.Empty;

  /// <summary>The QR code as a PNG data URI, ready for an image's src.</summary>
  public string QrCode { get; init; } = string.Empty;
}

public sealed record TwoFactorEnable
{
  public string Code { get; init; } = string.Empty;
}

/// <summary>Recovery codes to show once. Empty when enabling two-factor kept the codes the user already had.</summary>
public sealed record RecoveryCodeSet
{
  public IReadOnlyList<string> Codes { get; init; } = [];
}

public sealed record TwoFactorSignIn
{
  public string Code { get; init; } = string.Empty;

  /// <summary>Keep the session after the browser closes, as chosen on the first step.</summary>
  public bool RememberMe { get; init; }

  /// <summary>Skip the code on this browser next time.</summary>
  public bool RememberBrowser { get; init; }
}

public sealed record RecoveryCodeSignIn
{
  public string RecoveryCode { get; init; } = string.Empty;
}
