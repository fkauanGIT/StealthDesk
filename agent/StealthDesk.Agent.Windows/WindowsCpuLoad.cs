using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using StealthDesk.Agent.Core.Inventory;

namespace StealthDesk.Agent.Windows;

/// <summary>
/// Whole-machine processor load, measured by comparing Windows' idle and busy time between two samples.
/// </summary>
[SupportedOSPlatform("windows6.1")]
public sealed class WindowsCpuLoad(TimeProvider clock) : BackgroundService, ICpuLoad
{
  private static readonly TimeSpan _sampleEvery = TimeSpan.FromSeconds(5);
  private double _current;

  public double Current => Volatile.Read(ref _current);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    var previous = WindowsSystem.ProcessorTimes();
    using var timer = new PeriodicTimer(_sampleEvery, clock);

    try
    {
      while (await timer.WaitForNextTickAsync(stoppingToken))
      {
        var now = WindowsSystem.ProcessorTimes();
        Volatile.Write(ref _current, CpuTimes.BusyShare(previous, now));
        previous = now;
      }
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
    }
  }
}
