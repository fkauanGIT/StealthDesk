using Microsoft.Extensions.Time.Testing;
using StealthDesk.Contracts.Devices;
using StealthDesk.Core.Security;

namespace StealthDesk.Shared.Tests;

public class MessageSignerTests
{
  private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
  private readonly MessageSigner _signer;

  public MessageSignerTests()
  {
    _signer = new MessageSigner(_clock);
  }

  [Fact]
  public void CreateKeys_ReturnsA32BytePublicKeyThatMatchesThePrivateKey()
  {
    var keys = _signer.CreateKeys();

    Assert.True(MessageSigner.IsWellFormedPublicKey(keys.PublicKey));
    Assert.Equal(keys.PublicKey, _signer.PublicKeyOf(keys.PrivateKey));
  }

  [Fact]
  public void CreateKeys_NeverRepeats()
  {
    Assert.NotEqual(_signer.CreateKeys().PrivateKey, _signer.CreateKeys().PrivateKey);
  }

  [Fact]
  public void Sign_RecordsTheSignerKeyAndTheCurrentTime()
  {
    var keys = _signer.CreateKeys();

    var envelope = _signer.Sign(SampleReport(), keys.PrivateKey);

    Assert.Equal(keys.PublicKey, envelope.SignerKey);
    Assert.Equal(_clock.GetUtcNow(), envelope.SignedAt);
    Assert.Equal(64, envelope.Signature.Length);
  }

  [Fact]
  public void HasValidSignature_AcceptsAnUntouchedEnvelope()
  {
    var keys = _signer.CreateKeys();
    var envelope = _signer.Sign(SampleReport(), keys.PrivateKey);

    Assert.True(_signer.HasValidSignature(envelope, keys.PublicKey));
  }

  [Fact]
  public void HasValidSignature_RejectsAChangedPayload()
  {
    var keys = _signer.CreateKeys();
    var envelope = _signer.Sign(SampleReport(), keys.PrivateKey);

    var tampered = envelope with { Payload = envelope.Payload with { MachineName = "SOMEONE-ELSE" } };

    Assert.False(_signer.HasValidSignature(tampered, keys.PublicKey));
  }

  [Fact]
  public void HasValidSignature_RejectsAChangedSigningTime()
  {
    var keys = _signer.CreateKeys();
    var envelope = _signer.Sign(SampleReport(), keys.PrivateKey);

    var replayed = envelope with { SignedAt = envelope.SignedAt.AddMinutes(10) };

    Assert.False(_signer.HasValidSignature(replayed, keys.PublicKey));
  }

  [Fact]
  public void HasValidSignature_RejectsAnotherDevicesKey()
  {
    var envelope = _signer.Sign(SampleReport(), _signer.CreateKeys().PrivateKey);

    Assert.False(_signer.HasValidSignature(envelope, _signer.CreateKeys().PublicKey));
  }

  [Fact]
  public void HasValidSignature_RejectsAnEnvelopeThatClaimsAnotherSignerKey()
  {
    var keys = _signer.CreateKeys();
    var envelope = _signer.Sign(SampleReport(), keys.PrivateKey);

    // The claimed key is part of what was signed, so swapping it breaks the signature.
    var impostor = envelope with { SignerKey = _signer.CreateKeys().PublicKey };

    Assert.False(_signer.HasValidSignature(impostor, keys.PublicKey));
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("not base64!")]
  [InlineData("AAAA")]
  public void HasValidSignature_RejectsMalformedKeys(string? publicKey)
  {
    var envelope = _signer.Sign(SampleReport(), _signer.CreateKeys().PrivateKey);

    Assert.False(_signer.HasValidSignature(envelope, publicKey!));
    Assert.False(MessageSigner.IsWellFormedPublicKey(publicKey));
  }

  [Theory]
  [InlineData(0, true)]
  [InlineData(59, true)]
  [InlineData(-59, true)]
  [InlineData(61, false)]
  [InlineData(-61, false)]
  public void IsRecent_ComparesTheSigningTimeWithTheClock(int secondsLater, bool expected)
  {
    var envelope = _signer.Sign(SampleReport(), _signer.CreateKeys().PrivateKey);

    // Negative values mean the sender's clock runs ahead of the server's.
    var received = envelope with { SignedAt = _clock.GetUtcNow().AddSeconds(-secondsLater) };

    Assert.Equal(expected, _signer.IsRecent(received, TimeSpan.FromMinutes(1)));
  }

  private static DeviceReport SampleReport() => new()
  {
    DeviceId = Guid.NewGuid(),
    MachineName = "OFFICE-PC",
    Platform = DevicePlatform.Windows,
    CpuCores = 8,
    Disks = [new DiskInfo { Name = @"C:\", SizeGb = 512, FreeGb = 200 }],
  };
}
