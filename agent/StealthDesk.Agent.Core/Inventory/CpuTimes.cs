namespace StealthDesk.Agent.Core.Inventory;

/// <summary>Processor time counters at one instant: time spent idle and total time, over all cores.</summary>
public readonly record struct CpuTimes(ulong Idle, ulong Total)
{
  /// <summary>Share of the time between two samples the processor was busy, from 0 to 1.</summary>
  public static double BusyShare(CpuTimes before, CpuTimes after)
  {
    if (after.Total <= before.Total)
    {
      return 0;
    }

    var total = (double)(after.Total - before.Total);
    var idle = after.Idle >= before.Idle ? after.Idle - before.Idle : 0;
    return Math.Clamp(1 - idle / total, 0, 1);
  }
}
