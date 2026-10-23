using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

public sealed class KeyProtectionTests
{
  private const string Password = "pfx-password";

  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  // Without encryption the key's secret is right there in the XML; with it, only the certificate can open it.
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task TheStoredKeys_HoldNoPlainKeyMaterial_OnlyWhenEncrypted(bool encrypted)
  {
    using var certificate = NewCertificate();
    using var server = encrypted ? Encrypted(ServerHost.InMemory(), certificate) : ServerHost.InMemory();
    Protect(server);

    var xml = await StoredKeysAsync(server);

    Assert.Equal(!encrypted, xml.Contains("<masterKey", StringComparison.Ordinal));
    Assert.Equal(encrypted, xml.Contains("EncryptedData", StringComparison.Ordinal));
  }

  [Fact]
  public async Task ACookieIssuedBeforeARestart_IsAcceptedAfterIt()
  {
    using var certificate = NewCertificate();
    var file = Path.Combine(Path.GetTempPath(), $"stealthdesk-keys-{Guid.NewGuid():N}.pfx");
    await File.WriteAllBytesAsync(file, certificate.Export(X509ContentType.Pkcs12, Password), Cancel);
    try
    {
      using var server = ServerHost.InMemory()
        .With("KeyProtection:EncryptKeys", "true")
        .With("KeyProtection:Certificate:Path", file)
        .With("KeyProtection:Certificate:Password", Password);
      var cookie = await SignedInCookieAsync(server);

      using var restarted = server.Restarted();

      Assert.Equal(HttpStatusCode.OK, await MeAsync(restarted, cookie));
    }
    finally
    {
      File.Delete(file);
    }
  }

  // Data protection only uses the certificate's key pair, so an expired one keeps working, as the guide says.
  [Fact]
  public async Task AnExpiredCertificate_StillEncryptsAndReadsTheKeys()
  {
    using var rsa = RSA.Create(2048);
    using var expired = new CertificateRequest("CN=Expired keys", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
      .CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddYears(-1));
    using var server = Encrypted(ServerHost.InMemory(), expired);
    var cookie = await SignedInCookieAsync(server);

    using var restarted = server.Restarted();

    Assert.Contains("EncryptedData", await StoredKeysAsync(restarted), StringComparison.Ordinal);
    Assert.Equal(HttpStatusCode.OK, await MeAsync(restarted, cookie));
  }

  [Fact]
  public async Task KeysWrittenBeforeEncryption_KeepWorking_AndNewKeysAreEncrypted()
  {
    using var certificate = NewCertificate();
    using var plain = ServerHost.InMemory();
    var cookie = await SignedInCookieAsync(plain);

    using var encrypted = Encrypted(plain.Restarted(), certificate);
    Assert.Equal(HttpStatusCode.OK, await MeAsync(encrypted, cookie));

    var now = DateTimeOffset.UtcNow;
    var created = encrypted.Services.GetRequiredService<IKeyManager>().CreateNewKey(now, now.AddDays(90));
    var keys = await encrypted.WithDbAsync(db => db.DataProtectionKeys.ToListAsync(Cancel));

    Assert.Equal(2, keys.Count);
    Assert.Contains("<masterKey", keys.Single(x => x.FriendlyName != $"key-{created.KeyId}").Xml, StringComparison.Ordinal);
    Assert.DoesNotContain("<masterKey", keys.Single(x => x.FriendlyName == $"key-{created.KeyId}").Xml, StringComparison.Ordinal);
  }

  // A new certificate encrypts from now on; the old one, kept as a previous certificate, still reads the keys it
  // encrypted. Dropped too soon, every session made with those keys ends, though signing in again works.
  [Theory]
  [InlineData(true, HttpStatusCode.OK)]
  [InlineData(false, HttpStatusCode.Unauthorized)]
  public async Task AfterRotatingTheCertificate_OldSessionsWork_WhileTheOldCertificateIsKept(bool keepOld, HttpStatusCode expected)
  {
    using var old = NewCertificate("Old keys");
    using var current = NewCertificate("New keys");
    using var server = Encrypted(ServerHost.InMemory(), old);
    var cookie = await SignedInCookieAsync(server, "rotation@example.com");

    using var rotated = Encrypted(server.Restarted(), current);
    if (keepOld)
    {
      rotated
        .With("KeyProtection:PreviousCertificates:0:Base64", Pfx(old))
        .With("KeyProtection:PreviousCertificates:0:Password", Password);
    }

    Assert.Equal(expected, await MeAsync(rotated, cookie));
    Assert.Equal(HttpStatusCode.OK, await MeAsync(rotated, await TestAccounts.SignInForCookieAsync(rotated, "rotation@example.com")));
  }

  [Theory]
  [InlineData("no certificate", "KeyProtection:EncryptKeys is on, but there is no certificate")]
  [InlineData("missing file", "KeyProtection:Certificate:Path is '")]
  [InlineData("wrong password", "KeyProtection:Certificate can't be read: KeyProtection:Certificate:Password is wrong")]
  [InlineData("not base64", "KeyProtection:Certificate:Base64 isn't base64")]
  [InlineData("not a pfx", "KeyProtection:Certificate can't be read")]
  [InlineData("no private key", "KeyProtection:Certificate has no RSA private key")]
  [InlineData("not RSA", "KeyProtection:Certificate has no RSA private key")]
  [InlineData("bad previous", "KeyProtection:PreviousCertificates:0 can't be read")]
  [InlineData("empty previous", "KeyProtection:PreviousCertificates:0 names no certificate")]
  [InlineData("not a switch", "KeyProtection:EncryptKeys")]
  public void ACertificateTheServerCantUse_StopsIt(string problem, string message)
  {
    using var certificate = NewCertificate();
    using var server = ServerHost.InMemory().With("KeyProtection:EncryptKeys", "true");
    switch (problem)
    {
      case "no certificate":
        break;
      case "missing file":
        server.With("KeyProtection:Certificate:Path", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.pfx"));
        break;
      case "wrong password":
        server.With("KeyProtection:Certificate:Base64", Pfx(certificate)).With("KeyProtection:Certificate:Password", "wrong");
        break;
      case "not base64":
        server.With("KeyProtection:Certificate:Base64", "not base64!");
        break;
      case "not a pfx":
        server.With("KeyProtection:Certificate:Base64", Convert.ToBase64String("not a certificate"u8.ToArray()));
        break;
      case "no private key":
        using (var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData))
        {
          server.With("KeyProtection:Certificate:Base64", Pfx(publicOnly)).With("KeyProtection:Certificate:Password", Password);
        }

        break;
      case "not RSA":
        using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        using (var other = new CertificateRequest("CN=Elliptic", ecdsa, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1)))
        {
          server.With("KeyProtection:Certificate:Base64", Pfx(other)).With("KeyProtection:Certificate:Password", Password);
        }

        break;
      case "bad previous":
        Encrypted(server, certificate).With("KeyProtection:PreviousCertificates:0:Base64", Pfx(certificate)).With("KeyProtection:PreviousCertificates:0:Password", "wrong");
        break;
      case "empty previous":
        Encrypted(server, certificate).With("KeyProtection:PreviousCertificates:0:Password", Password);
        break;
      case "not a switch":
        server.With("KeyProtection:EncryptKeys", "sometimes");
        break;
    }

    Assert.Contains(message, server.RefusalToStart(), StringComparison.Ordinal);
  }

  // A file the server's user may not read is a permission to fix, not a password: the message says which.
  [Theory]
  [InlineData("KeyProtection:Certificate")]
  [InlineData("KeyProtection:PreviousCertificates:0")]
  public void ACertificateFileTheServerMayNotRead_StopsIt_SayingSo(string key)
  {
    using var certificate = NewCertificate();
    using var file = UnreadableFile.Create(certificate.Export(X509ContentType.Pkcs12, Password));
    using var server = Encrypted(ServerHost.InMemory(), certificate).With($"{key}:Base64", string.Empty)
      .With($"{key}:Path", file.Path).With($"{key}:Password", Password);

    var refusal = server.RefusalToStart();

    Assert.Contains($"{key}:Path is '{file.Path}', which the server may not read: permission denied for the user it runs as, {Environment.UserName}", refusal, StringComparison.Ordinal);
    Assert.DoesNotContain("Password is wrong", refusal, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(false, true)]
  [InlineData(true, false)]
  public async Task InProduction_UnencryptedKeys_AreWarnedAbout(bool encrypted, bool warned)
  {
    using var certificate = NewCertificate();
    var logs = new CapturedLogs();
    using var server = (await ServerHost.OnPostgresAsync())
      .InEnvironment("Production")
      .WithServices(services => services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs));
    if (encrypted)
    {
      Encrypted(server, certificate);
    }

    _ = server.Services;

    Assert.Equal(warned, logs.Warnings.Any(x => x.Contains("KeyProtection:EncryptKeys", StringComparison.Ordinal)));
  }

  private static X509Certificate2 NewCertificate(string name = "StealthDesk keys")
  {
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
  }

  private static string Pfx(X509Certificate2 certificate) => Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, Password));

  private static ServerHost Encrypted(ServerHost server, X509Certificate2 certificate) => server
    .With("KeyProtection:EncryptKeys", "true")
    .With("KeyProtection:Certificate:Base64", Pfx(certificate))
    .With("KeyProtection:Certificate:Password", Password);

  // Protecting anything makes the server create its first key.
  private static void Protect(ServerHost server) =>
    server.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("secret");

  private static async Task<string> StoredKeysAsync(ServerHost server) =>
    string.Concat(await server.WithDbAsync(db => db.DataProtectionKeys.Select(x => x.Xml).ToListAsync(Cancel)));

  private static async Task<string> SignedInCookieAsync(ServerHost server, string email = "keys@example.com")
  {
    await TestAccounts.CreateUserAsync(server, email);
    return await TestAccounts.SignInForCookieAsync(server, email);
  }

  private static async Task<HttpStatusCode> MeAsync(ServerHost server, string cookie)
  {
    using var client = TestAccounts.Client(server);
    using var response = await client.SendAsync(TestAccounts.WithCookie(HttpMethod.Get, Routes.CurrentUser, cookie), Cancel);
    return response.StatusCode;
  }

  /// <summary>
  /// A file the test's own user may not read, as a certificate given to another user is to the server: denied by an
  /// access rule on Windows, by its mode elsewhere. Readable again, and deleted, once disposed.
  /// </summary>
  private sealed class UnreadableFile : IDisposable
  {
    private UnreadableFile(string path) => Path = path;

    public string Path { get; }

    public static UnreadableFile Create(byte[] contents)
    {
      var file = new UnreadableFile(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"unreadable-{Guid.NewGuid():N}.pfx"));
      File.WriteAllBytes(file.Path, contents);
      file.Deny(true);

      // Root reads whatever the mode says: such a run can't show a denied read.
      var denied = false;
      try
      {
        File.ReadAllBytes(file.Path);
      }
      catch (UnauthorizedAccessException)
      {
        denied = true;
      }

      if (!denied)
      {
        file.Dispose();
        Assert.Skip("This user reads the file whatever its permissions say, as root does.");
      }

      return file;
    }

    public void Dispose()
    {
      Deny(false);
      File.Delete(Path);
    }

    private void Deny(bool deny)
    {
      if (OperatingSystem.IsWindows())
      {
        var info = new FileInfo(Path);
        var acl = info.GetAccessControl();
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
          System.Security.Principal.WindowsIdentity.GetCurrent().User!,
          System.Security.AccessControl.FileSystemRights.ReadData,
          System.Security.AccessControl.AccessControlType.Deny);
        if (deny)
        {
          acl.AddAccessRule(rule);
        }
        else
        {
          acl.RemoveAccessRule(rule);
        }

        info.SetAccessControl(acl);
      }
      else
      {
        File.SetUnixFileMode(Path, deny ? UnixFileMode.None : UnixFileMode.UserRead | UnixFileMode.UserWrite);
      }
    }
  }
}
