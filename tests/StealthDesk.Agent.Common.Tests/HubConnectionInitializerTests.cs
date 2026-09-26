using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StealthDesk.Agent.Common.Services;
using StealthDesk.Agent.Shared.Services;
using StealthDesk.Libraries.Api.Contracts.Hubs;
using StealthDesk.Libraries.Signalr.Client;

namespace StealthDesk.Agent.Common.Tests;

public class HubConnectionInitializerTests
{
  private static readonly TimeSpan _maxJitter = TimeSpan.FromSeconds(20);

  [Theory]
  [InlineData(1, 1)]
  [InlineData(2, 4)]
  [InlineData(5, 25)]
  [InlineData(13, 169)]
  [InlineData(14, 180)]
  [InlineData(100, 180)]
  public void GetNextRetryDelay_GrowsWithTheAttemptAndIsCappedAt180SecondsPlusJitter(long attempt, int expectedBaseSeconds)
  {
    var initializer = CreateInitializer();
    var expectedBase = TimeSpan.FromSeconds(expectedBaseSeconds);

    for (var i = 0; i < 50; i++)
    {
      var delay = GetNextRetryDelay(initializer, attempt);

      Assert.InRange(delay, expectedBase, expectedBase + _maxJitter);
    }
  }

  [Fact]
  public void GetNextRetryDelay_AddsRandomJitter()
  {
    var initializer = CreateInitializer();

    var delays = Enumerable.Range(0, 20)
      .Select(_ => GetNextRetryDelay(initializer, attempt: 14))
      .Distinct()
      .Count();

    // Without jitter every agent would retry at the same instant after a server restart.
    Assert.True(delays > 1, "Every retry delay was identical.");
  }

  private static HubConnectionInitializer CreateInitializer()
  {
    return new HubConnectionInitializer(
      TimeProvider.System,
      Mock.Of<IHubConnection<IAgentHub>>(),
      Mock.Of<IOptionsAccessor>(),
      Mock.Of<IAgentHeartbeatTimer>(),
      NullLogger<HubConnectionInitializer>.Instance);
  }

  private static TimeSpan GetNextRetryDelay(HubConnectionInitializer initializer, long attempt)
  {
    var method = typeof(HubConnectionInitializer).GetMethod(
      "GetNextRetryDelay",
      BindingFlags.Instance | BindingFlags.NonPublic)
      ?? throw new InvalidOperationException("GetNextRetryDelay was not found.");

    return (TimeSpan)method.Invoke(initializer, [attempt])!;
  }
}
