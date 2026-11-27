using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.Agent.Core.Wake;
using StealthDesk.Core;

namespace StealthDesk.Agent.Tests;

/// <summary>Builds a Wake-on-LAN magic packet and hands it to whatever broadcasts it, never opening a real socket.</summary>
public class WakeSenderTests
{
  [Theory]
  [InlineData("00155D012345")]
  [InlineData("00:15:5D:01:23:45")]
  [InlineData("00-15-5D-01-23-45")]
  public async Task SendMagicPacket_BroadcastsTheCorrectPacket_WhateverTheMacsFormat(string macAddress)
  {
    byte[]? sent = null;
    var sender = new WakeSender(NullLogger<WakeSender>.Instance, packet => sent = packet);

    var result = await sender.SendMagicPacket(macAddress);

    Assert.True(result.Succeeded, result.Error);
    Assert.NotNull(sent);
    Assert.Equal(102, sent.Length);
    Assert.Equal(Enumerable.Repeat((byte)0xFF, 6), sent[..6]);
    var mac = new byte[] { 0x00, 0x15, 0x5D, 0x01, 0x23, 0x45 };
    for (var i = 0; i < 16; i++)
    {
      Assert.Equal(mac, sent[(6 + i * 6)..(6 + i * 6 + 6)]);
    }
  }

  [Theory]
  [InlineData("not a mac address")]
  [InlineData("00155D01234")] // 11 hex digits: one short of a MAC
  [InlineData("")]
  public async Task SendMagicPacket_Fails_ForAnythingNotAMacAddress(string macAddress)
  {
    var broadcast = false;
    var sender = new WakeSender(NullLogger<WakeSender>.Instance, _ => broadcast = true);

    var result = await sender.SendMagicPacket(macAddress);

    Assert.False(result.Succeeded);
    Assert.False(broadcast);
  }
}
