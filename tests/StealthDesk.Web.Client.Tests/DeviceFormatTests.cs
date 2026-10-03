using System.Runtime.InteropServices;
using StealthDesk.Web.Client.Devices;

namespace StealthDesk.Web.Client.Tests;

public class DeviceFormatTests
{
  [Theory]
  [InlineData(0, "0%")]
  [InlineData(0.574, "57%")]
  [InlineData(1, "100%")]
  [InlineData(1.4, "100%")]
  [InlineData(double.NaN, "0%")]
  public void Percent_IsWholeAndClamped(double share, string expected) => Assert.Equal(expected, DeviceFormat.Percent(share));

  [Theory]
  [InlineData(7.25, 16, "7.3 / 16 GB")]
  [InlineData(210, 512, "210 / 512 GB")]
  [InlineData(3, 0, DeviceFormat.Missing)]
  public void Usage_ShowsUsedOverTotal(double used, double total, string expected) =>
    Assert.Equal(expected, DeviceFormat.Usage(used, total));

  [Fact]
  public void Share_OfNoTotal_IsZero() => Assert.Equal(0, DeviceFormat.Share(5, 0));

  [Theory]
  [InlineData(null, DeviceFormat.Missing)]
  [InlineData("  ", DeviceFormat.Missing)]
  [InlineData("alice", "alice")]
  public void OrMissing_ReplacesBlanks(string? value, string expected) => Assert.Equal(expected, DeviceFormat.OrMissing(value));

  [Fact]
  public void Architecture_IsLowerCase() => Assert.Equal("x64", DeviceFormat.Architecture(Architecture.X64));
}
