using StealthDesk.Core;

namespace StealthDesk.Shared.Tests;

public class BackoffTests
{
  [Theory]
  [InlineData(1, 2)]
  [InlineData(2, 4)]
  [InlineData(3, 8)]
  [InlineData(7, 128)]
  [InlineData(8, 180)]
  [InlineData(1000, 180)]
  public void DelayFor_DoublesUntilTheCeiling(int attempt, int expectedSeconds)
  {
    var backoff = new Backoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(180), TimeSpan.Zero);

    Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), backoff.DelayFor(attempt));
  }

  [Fact]
  public void DelayFor_AddsASpreadWithinTheLimit()
  {
    var backoff = new Backoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(15));

    var delays = Enumerable.Range(0, 50).Select(_ => backoff.DelayFor(8)).ToList();

    Assert.All(delays, delay => Assert.InRange(delay, TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(195)));
    Assert.True(delays.Distinct().Count() > 1, "Every delay was identical, so clients would retry in lockstep.");
  }

  [Fact]
  public void DelayFor_RejectsAttemptsBelowOne()
  {
    var backoff = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.Zero);

    Assert.Throws<ArgumentOutOfRangeException>(() => backoff.DelayFor(0));
  }
}
