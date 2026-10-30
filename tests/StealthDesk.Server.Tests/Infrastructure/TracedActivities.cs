using System.Collections.Concurrent;
using System.Diagnostics;
using StealthDesk.Web.Server.Dashboard;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>The device access activities stopped while it listens; other tests' too, so tests pick theirs by device.</summary>
public sealed class TracedActivities : IDisposable
{
  private readonly ConcurrentQueue<Activity> _stopped = new();
  private readonly ActivityListener _listener;

  public TracedActivities()
  {
    _listener = new ActivityListener
    {
      ShouldListenTo = source => source.Name == DeviceAccessTracing.SourceName,
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
      ActivityStopped = _stopped.Enqueue,
    };
    ActivitySource.AddActivityListener(_listener);
  }

  public IReadOnlyList<Activity> All => [.. _stopped];

  public IReadOnlyList<Activity> Of(Guid device) =>
    [.. _stopped.Where(x => Equals(x.GetTagItem(DeviceAccessTracing.DeviceIdTag), device.ToString()))];

  public void Dispose() => _listener.Dispose();
}
