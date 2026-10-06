using System.Buffers.Binary;
using System.Security.Cryptography;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>
/// What an authenticator app does (RFC 6238): HMAC-SHA1 of the 30-second time step, cut down to six digits.
/// </summary>
public static class Totp
{
  private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

  /// <param name="sharedKey">The key as the setup page shows it, spaces and case included.</param>
  public static string Code(string sharedKey, DateTimeOffset? at = null)
  {
    Span<byte> step = stackalloc byte[8];
    BinaryPrimitives.WriteInt64BigEndian(step, (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30);
    var hash = HMACSHA1.HashData(Base32(sharedKey), step);
    var offset = hash[^1] & 0x0F;
    var value = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7FFFFFFF;
    return (value % 1_000_000).ToString("D6");
  }

  private static byte[] Base32(string text)
  {
    var bits = 0;
    var buffer = 0;
    var bytes = new List<byte>();
    foreach (var c in text.Replace(" ", string.Empty).ToUpperInvariant())
    {
      buffer = (buffer << 5) | Alphabet.IndexOf(c);
      bits += 5;
      if (bits >= 8)
      {
        bytes.Add((byte)(buffer >> (bits - 8)));
        bits -= 8;
        buffer &= (1 << bits) - 1;
      }
    }

    return [.. bytes];
  }
}
