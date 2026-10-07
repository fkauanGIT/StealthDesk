using System.Buffers.Binary;
using System.Security.Cryptography;

namespace StealthDesk.Web.UITests.Infrastructure;

/// <summary>What an authenticator app does (RFC 6238), from the key the setup page shows.</summary>
public static class Totp
{
  private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

  public static string Code(string sharedKey)
  {
    Span<byte> step = stackalloc byte[8];
    BinaryPrimitives.WriteInt64BigEndian(step, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
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
