using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using StealthDesk.Branding;

namespace StealthDesk.Web.Server.KeyProtection;

public static class KeyProtectionSetup
{
  /// <summary>
  /// Data protection with its keys kept in the database, so cookies stay valid after a restart and across servers, and
  /// encrypted there with a certificate when <see cref="KeyProtectionOptions.EncryptKeys"/> is on. Every certificate is
  /// loaded now: one the server couldn't read keys back with stops it here, not at the first sign-in after a restart.
  /// </summary>
  public static WebApplicationBuilder AddStealthDeskDataProtection(this WebApplicationBuilder builder)
  {
    var section = builder.Configuration.GetSection(KeyProtectionOptions.Section);
    var settings = section.Get<KeyProtectionOptions>() ?? new KeyProtectionOptions();
    builder.Services.AddOptions<KeyProtectionOptions>().Bind(section);

    var dataProtection = builder.Services
      .AddDataProtection()
      .SetApplicationName(Brand.Name)
      .PersistKeysToDbContext<StealthDeskDb>();

    var previous = settings.PreviousCertificates
      .Select((certificate, index) => Load(certificate, KeyProtectionOptions.Key($"{nameof(KeyProtectionOptions.PreviousCertificates)}:{index}")))
      .ToArray();
    if (previous.Length > 0)
    {
      dataProtection.UnprotectKeysWithAnyCertificate(previous);
    }

    X509Certificate2? current = null;
    if (settings.EncryptKeys)
    {
      var key = KeyProtectionOptions.Key(nameof(KeyProtectionOptions.Certificate));
      if (settings.Certificate is not { IsSet: true })
      {
        throw new InvalidOperationException(
          $"{KeyProtectionOptions.Key(nameof(KeyProtectionOptions.EncryptKeys))} is on, but there is no certificate to encrypt with: " +
          $"set {key}:{nameof(KeyCertificate.Path)} to a .pfx file, or {key}:{nameof(KeyCertificate.Base64)} to its contents.");
      }

      current = Load(settings.Certificate, key);
      dataProtection.ProtectKeysWithCertificate(current);
    }

    builder.Services.AddSingleton(new KeyProtectionStatus(current?.Thumbprint, previous.Length));
    builder.Services.AddHostedService<KeyProtectionReport>();
    return builder;
  }

  // The ASP.NET images name their unprivileged user's id in APP_UID: the number a chown of the file needs.
  private static string RunningAs =>
    Environment.GetEnvironmentVariable("APP_UID") is { Length: > 0 } uid ? $"{Environment.UserName} (uid {uid})" : Environment.UserName;

  private static X509Certificate2 Load(KeyCertificate certificate, string key)
  {
    if (!certificate.IsSet)
    {
      throw new InvalidOperationException($"{key} names no certificate: set its {nameof(KeyCertificate.Path)} or {nameof(KeyCertificate.Base64)}.");
    }

    X509Certificate2 loaded;
    try
    {
      if (!string.IsNullOrWhiteSpace(certificate.Base64))
      {
        loaded = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(certificate.Base64), certificate.Password);
      }
      else if (File.Exists(certificate.Path))
      {
        loaded = X509CertificateLoader.LoadPkcs12FromFile(certificate.Path, certificate.Password);
      }
      else
      {
        throw new InvalidOperationException($"{key}:{nameof(KeyCertificate.Path)} is '{certificate.Path}', which doesn't exist.");
      }
    }
    catch (FormatException)
    {
      throw new InvalidOperationException($"{key}:{nameof(KeyCertificate.Base64)} isn't base64.");
    }
    // A file the server may not open comes wrapped in a CryptographicException, like a wrong password: told apart
    // here, so the message points at the file's permissions and not at the password.
    catch (Exception ex) when (ex is UnauthorizedAccessException || ex.InnerException is UnauthorizedAccessException)
    {
      throw new InvalidOperationException(
        $"{key}:{nameof(KeyCertificate.Path)} is '{certificate.Path}', which the server may not read: permission denied for the " +
        $"user it runs as, {RunningAs}. Let that user read the file.", ex);
    }
    catch (CryptographicException ex)
    {
      throw new InvalidOperationException(
        $"{key} can't be read: {key}:{nameof(KeyCertificate.Password)} is wrong, or it isn't a .pfx file. {ex.Message}", ex);
    }

    // Encrypting needs only the public key, so without the private one the server would start and write keys it
    // can't read back after the next restart.
    using var privateKey = loaded.GetRSAPrivateKey();
    if (privateKey is null)
    {
      loaded.Dispose();
      throw new InvalidOperationException(
        $"{key} has no RSA private key, so the keys it encrypts couldn't be read back. Export it with its private key.");
    }

    return loaded;
  }
}

/// <summary>What the keys are encrypted with: the current certificate's thumbprint, and how many only read old keys.</summary>
public sealed record KeyProtectionStatus(string? Thumbprint, int PreviousCertificates);

/// <summary>Says at startup how the keys are stored, and warns in Production when they aren't encrypted.</summary>
public sealed class KeyProtectionReport(KeyProtectionStatus status, IHostEnvironment environment, ILogger<KeyProtectionReport> logger) : IHostedService
{
  public Task StartAsync(CancellationToken cancellationToken)
  {
    if (status.Thumbprint is { } thumbprint)
    {
      logger.LogInformation(
        "Data protection keys are encrypted with the certificate {Thumbprint}; {Previous} previous certificates read older keys.",
        thumbprint,
        status.PreviousCertificates);
    }
    else if (environment.IsProduction())
    {
      logger.LogWarning(
        "Data protection keys are stored unencrypted: anyone who reads the database or a backup of it can forge sign-in cookies. Turn on {Setting} with a certificate.",
        KeyProtectionOptions.Key(nameof(KeyProtectionOptions.EncryptKeys)));
    }

    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
