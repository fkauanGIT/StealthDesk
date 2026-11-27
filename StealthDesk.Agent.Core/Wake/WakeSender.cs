using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using StealthDesk.Core;

namespace StealthDesk.Agent.Core.Wake;

/// <summary>
/// Sends Wake-on-LAN magic packets. Needs nothing platform-specific: a UDP broadcast works the same on every OS
/// the agent runs on, unlike <see cref="Power.IPowerControl"/>.
/// </summary>
public sealed class WakeSender : IWakeSender
{
  private const int Port = 9;
  private readonly ILogger<WakeSender> _logger;
  private readonly Action<byte[]> _broadcast;

  public WakeSender(ILogger<WakeSender> logger) : this(logger, Broadcast)
  {
  }

  // The real socket is never opened by a test: it would depend on the test machine's own network stack. Tests use
  // this constructor to substitute a broadcast that only records the packet it was asked to send.
  internal WakeSender(ILogger<WakeSender> logger, Action<byte[]> broadcast)
  {
    _logger = logger;
    _broadcast = broadcast;
  }

  public Task<Outcome<bool>> SendMagicPacket(string macAddress)
  {
    if (!TryBuildPacket(macAddress, out var packet))
    {
      return Task.FromResult(Outcome.Failure<bool>("Not a MAC address."));
    }

    try
    {
      _broadcast(packet);
      return Task.FromResult(Outcome.Success(true));
    }
    catch (SocketException ex)
    {
      _logger.LogError(ex, "Failed to broadcast a Wake-on-LAN packet.");
      return Task.FromResult(Outcome.Failure<bool>("The device couldn't send the wake packet."));
    }
  }

  // 6 bytes of 0xFF, then the target's MAC address repeated 16 times.
  internal static bool TryBuildPacket(string macAddress, out byte[] packet)
  {
    packet = [];
    var hex = new string(macAddress.Where(Uri.IsHexDigit).ToArray());
    if (hex.Length != 12)
    {
      return false;
    }

    var mac = new byte[6];
    for (var i = 0; i < 6; i++)
    {
      mac[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
    }

    var built = new byte[6 + 16 * 6];
    Array.Fill(built, (byte)0xFF, 0, 6);
    for (var i = 0; i < 16; i++)
    {
      mac.CopyTo(built, 6 + i * 6);
    }

    packet = built;
    return true;
  }

  private static void Broadcast(byte[] packet)
  {
    using var client = new UdpClient();
    client.EnableBroadcast = true;
    client.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, Port));
  }
}
