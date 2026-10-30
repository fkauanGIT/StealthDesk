using Microsoft.Extensions.Time.Testing;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl;

namespace StealthDesk.Shared.Tests;

/// <summary>The quality frames are encoded with, and the bandwidth it follows.</summary>
public class QualityPolicyTests
{
  private static readonly CaptureSettings Auto = new(AutoQuality: true);

  [Fact]
  public void SetByHand_TheQualityIsTheOneSet_InItsRange()
  {
    Assert.Equal(75, QualityPolicy.For(new CaptureSettings(), mbpsOut: 50));
    Assert.Equal(100, QualityPolicy.For(new CaptureSettings(Quality: 150), mbpsOut: 0));
    Assert.Equal(1, QualityPolicy.For(new CaptureSettings(Quality: -3), mbpsOut: 0));
  }

  [Theory]
  [InlineData(0, 80)]
  [InlineData(5, 80)]
  [InlineData(7.5, 65)]
  [InlineData(10, 50)]
  [InlineData(15, 20)]
  [InlineData(40, 20)]
  public void Automatic_TheMoreIsSent_TheLowerTheQuality_BetweenItsBounds(double mbps, int quality)
  {
    Assert.Equal(quality, QualityPolicy.For(Auto, mbps));
  }

  [Fact]
  public void BoundsOutOfOrder_AreBroughtIntoOrder()
  {
    var settings = new CaptureSettings(AutoQualityMinimum: 90, AutoQualityMaximum: 10, AutoQualityLowerMbps: 8, AutoQualityUpperMbps: 2, MaxMbps: 0).Normalized();

    Assert.Equal((90, 91), (settings.AutoQualityMinimum, settings.AutoQualityMaximum));
    Assert.True(settings.AutoQualityUpperMbps > settings.AutoQualityLowerMbps);
    Assert.Equal(0.1, settings.MaxMbps);
  }

  [Theory]
  [InlineData(true, 3.75, true)]
  [InlineData(true, 3.8, false)]
  [InlineData(false, 0, false)]
  public void WhatWentOutWorse_IsSentAgain_OnlyWellUnderTheLowerBound(bool auto, double mbps, bool recovers)
  {
    Assert.Equal(recovers, QualityPolicy.MayRecover(new CaptureSettings(AutoQuality: auto), mbps));
  }

  [Fact]
  public void TheMeter_CountsTheLastFewSeconds_AsMegabitsPerSecond()
  {
    var clock = new FakeTimeProvider();
    var meter = new TransferMeter(clock, TimeSpan.FromSeconds(2));

    meter.Record(500_000);
    clock.Advance(TimeSpan.FromSeconds(1));
    meter.Record(250_000);

    Assert.Equal(3.0, meter.Mbps, precision: 6);
    clock.Advance(TimeSpan.FromSeconds(1.5));
    Assert.Equal(1.0, meter.Mbps, precision: 6);
    clock.Advance(TimeSpan.FromSeconds(1));
    Assert.Equal(0, meter.Mbps);
  }
}
